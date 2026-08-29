using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Gtools.Core;

public enum TranslationFailureKind { Network,Timeout,Forbidden,TooManyRequests,Server,InvalidResponse,Other }

public sealed class TranslationProviderException : InvalidOperationException
{
    public TranslationProviderException(string providerId,TranslationFailureKind kind,string message,HttpStatusCode? statusCode=null,
        DateTimeOffset? retryAfter=null,Exception? innerException=null):base(message,innerException)
    { ProviderId=providerId;Kind=kind;StatusCode=statusCode;RetryAfter=retryAfter; }
    public string ProviderId { get; }
    public TranslationFailureKind Kind { get; }
    public HttpStatusCode? StatusCode { get; }
    public DateTimeOffset? RetryAfter { get; }
}

public sealed record GoogleChannelTestResult(bool Success,string Message,HttpStatusCode? StatusCode,
    TranslationFailureKind? FailureKind,TimeSpan Elapsed,TimeSpan? CooldownRemaining);

public sealed class GoogleWebTranslationProvider : ITranslationProvider,IDisposable
{
    private const string Endpoint="https://translate.google.com/translate_a/single";
    private const string TestText="Gtools connectivity test";
    private const int CacheCapacity=64;
    private static readonly TimeSpan CacheLifetime=TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RequestInterval=TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan RetryDelay=TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DefaultRateLimitCooldown=TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan,CancellationToken,Task> _delay;
    private readonly object _stateGate=new();
    private readonly SemaphoreSlim _requestGate=new(1,1);
    private readonly CancellationTokenSource _lifetime=new();
    private readonly Dictionary<RequestKey,CacheEntry> _cache=new();
    private readonly Dictionary<RequestKey,Task<TranslationResult>> _inflight=new();
    private DateTimeOffset _nextRequestAt,_cooldownUntil;
    private bool _disposed;

    public string Id => "google-web";
    public string DisplayName => "Google 网站通道（非官方）";

    public GoogleWebTranslationProvider(HttpClient? httpClient=null)
        :this(httpClient,()=>DateTimeOffset.UtcNow,(delay,token)=>Task.Delay(delay,token)) { }

    internal GoogleWebTranslationProvider(HttpClient? httpClient,Func<DateTimeOffset> utcNow,
        Func<TimeSpan,CancellationToken,Task> delay)
    {
        _utcNow=utcNow;_delay=delay;
        if(httpClient is not null){_http=httpClient;return;}
        // Proxy=null + UseProxy=true 表示使用 .NET/Windows 默认代理发现；TUN 模式仍会在系统网络层接管。
        // 不设置任何绕过地址，也不保存代理信息。
        var handler=CreateDefaultHandler();
        _http=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(15)};_ownsHttp=true;
    }

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        if(string.IsNullOrWhiteSpace(request.Text))throw new ArgumentException("翻译文字不能为空。",nameof(request));
        var key=new RequestKey(request.Text,NormalizeLanguage(request.SourceLanguage),NormalizeLanguage(request.TargetLanguage));
        Task<TranslationResult> shared;TaskCompletionSource<TranslationResult>? completion=null;
        lock(_stateGate)
        {
            RemoveExpiredCacheEntries(_utcNow());
            if(_cache.TryGetValue(key,out var cached))
            {
                _cache[key]=cached with{LastAccess=_utcNow()};
                return cached.Result;
            }
            if(!_inflight.TryGetValue(key,out shared!))
            {
                completion=new(TaskCreationOptions.RunContinuationsAsynchronously);shared=completion.Task;
                _inflight[key]=shared;
            }
        }
        if(completion is not null)_=CompleteRequestAsync(key,request,completion);
        return await shared.WaitAsync(token);
    }

    /// <summary>用户明确表示已切换代理节点后，清除本地冷却并重新探测；不会自动调用。</summary>
    public Task<TranslationResult> TranslateAfterNetworkChangeAsync(TranslationRequest request,CancellationToken token=default)
    {
        lock(_stateGate)_cooldownUntil=DateTimeOffset.MinValue;
        return TranslateAsync(request,token);
    }

    public async Task<GoogleChannelTestResult> TestConnectionAsync(CancellationToken token=default)
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        var sw=Stopwatch.StartNew();
        try
        {
            await _requestGate.WaitAsync(token);
            try
            {
                // 连接测试由用户明确触发，允许在冷却期内发送一次固定合成文字，便于切换代理节点后立即验证。
                await WaitForRequestSlotAsync(token);
                _=await SendOnceAsync(new TranslationRequest(TestText,"en","zh-CN"),token);
            }
            finally{_requestGate.Release();}
            // A successful explicit probe means the user has restored connectivity (often by changing
            // the system/TUN exit). Do not keep an obsolete 429 cooldown after that confirmation.
            lock(_stateGate)_cooldownUntil=DateTimeOffset.MinValue;
            return new(true,$"Google 通道可用 · {sw.Elapsed.TotalSeconds:0.0}s",HttpStatusCode.OK,null,sw.Elapsed,null);
        }
        catch(TranslationProviderException ex)
        {
            var remaining=CooldownRemaining();
            return new(false,ex.Message,ex.StatusCode,ex.Kind,sw.Elapsed,remaining>TimeSpan.Zero?remaining:null);
        }
    }

    internal TimeSpan CooldownRemaining()
    {
        lock(_stateGate){var remaining=_cooldownUntil-_utcNow();return remaining>TimeSpan.Zero?remaining:TimeSpan.Zero;}
    }

    internal int CachedEntryCount{get{lock(_stateGate)return _cache.Count;}}
    internal int InflightRequestCount{get{lock(_stateGate)return _inflight.Count;}}

    internal static SocketsHttpHandler CreateDefaultHandler()=>new()
    {
        UseProxy=true,Proxy=null,AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate|DecompressionMethods.Brotli
    };

    private async Task CompleteRequestAsync(RequestKey key,TranslationRequest request,TaskCompletionSource<TranslationResult> completion)
    {
        try
        {
            var result=await ExecuteManagedAsync(request,_lifetime.Token);
            lock(_stateGate)
            {
                var now=_utcNow();_cache[key]=new(result,now+CacheLifetime,now);TrimCache();
            }
            completion.TrySetResult(result);
        }
        catch(OperationCanceledException) when(_lifetime.IsCancellationRequested){completion.TrySetCanceled(_lifetime.Token);}
        catch(Exception ex){completion.TrySetException(ex);}
        finally{lock(_stateGate)_inflight.Remove(key);}
    }

    private async Task<TranslationResult> ExecuteManagedAsync(TranslationRequest request,CancellationToken token)
    {
        await _requestGate.WaitAsync(token);
        try
        {
            DateTimeOffset cooldown;
            lock(_stateGate)cooldown=_cooldownUntil;
            if(cooldown>_utcNow())throw RateLimited(cooldown,null);

            for(var attempt=0;;attempt++)
            {
                try
                {
                    await WaitForRequestSlotAsync(token);
                    return await SendOnceAsync(request,token);
                }
                catch(TranslationProviderException ex) when(attempt==0&&ex.Kind is TranslationFailureKind.Timeout or TranslationFailureKind.Server)
                {
                    await _delay(RetryDelay,token);
                }
            }
        }
        finally{_requestGate.Release();}
    }

    private async Task WaitForRequestSlotAsync(CancellationToken token)
    {
        var wait=_nextRequestAt-_utcNow();
        if(wait>TimeSpan.Zero)await _delay(wait,token);
        _nextRequestAt=_utcNow()+RequestInterval;
    }

    private async Task<TranslationResult> SendOnceAsync(TranslationRequest request,CancellationToken token)
    {
        var sw=Stopwatch.StartNew();
        using var message=new HttpRequestMessage(HttpMethod.Get,BuildUrl(request));
        message.Headers.TryAddWithoutValidation("User-Agent","Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        message.Headers.TryAddWithoutValidation("Accept","application/json,text/plain,*/*");
        message.Headers.TryAddWithoutValidation("Accept-Language","zh-CN,zh;q=0.9,en;q=0.8");
        HttpResponseMessage response;
        try{response=await _http.SendAsync(message,HttpCompletionOption.ResponseContentRead,token);}
        catch(OperationCanceledException ex) when(!token.IsCancellationRequested)
        { throw Failure(TranslationFailureKind.Timeout,"Google 网站通道请求超时，请检查当前代理节点或网络后重试。",inner:ex); }
        catch(HttpRequestException ex)
        { throw Failure(TranslationFailureKind.Network,"无法连接 Google 网站通道，请检查代理节点、DNS 或网络设置。",inner:ex); }

        using(response)
        {
            if(!response.IsSuccessStatusCode)
            {
                if(response.StatusCode==HttpStatusCode.TooManyRequests)
                {
                    var until=ResolveRetryAfter(response,_utcNow());
                    lock(_stateGate)_cooldownUntil=until;
                    throw RateLimited(until,response.StatusCode);
                }
                throw Failure(KindOf(response.StatusCode),DescribeStatus(response.StatusCode),response.StatusCode);
            }
            var payload=await response.Content.ReadAsStringAsync(token);
            return new(DisplayName,ParsePayload(payload),sw.Elapsed,true);
        }
    }

    internal static string BuildUrl(TranslationRequest request) => Endpoint+
        "?client=gtx&dt=t&dj=1&ie=UTF-8&oe=UTF-8"+
        $"&sl={Uri.EscapeDataString(NormalizeLanguage(request.SourceLanguage))}"+
        $"&tl={Uri.EscapeDataString(NormalizeLanguage(request.TargetLanguage))}"+
        $"&q={Uri.EscapeDataString(request.Text)}";

    internal static string DescribeStatus(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Forbidden => "Google 网站通道拒绝访问（HTTP 403），请检查当前代理出口或稍后重试。",
        HttpStatusCode.TooManyRequests => "当前代理出口被 Google 网站通道限流（HTTP 429）。请等待冷却，或切换代理节点后重试。",
        >=HttpStatusCode.InternalServerError => $"Google 网站服务暂时异常（HTTP {(int)statusCode}），Gtools 已进行一次受控重试。",
        _ => $"Google 网站通道请求失败（HTTP {(int)statusCode}），请检查代理节点或手动切换翻译 Provider。"
    };

    internal static string ParsePayload(string payload)
    {
        try
        {
            using var doc=JsonDocument.Parse(payload);
            var root=doc.RootElement;
            var sb=new StringBuilder();
            if(root.ValueKind==JsonValueKind.Object&&root.TryGetProperty("sentences",out var sentences)&&sentences.ValueKind==JsonValueKind.Array)
            {
                foreach(var sentence in sentences.EnumerateArray())
                    if(sentence.ValueKind==JsonValueKind.Object&&sentence.TryGetProperty("trans",out var trans)&&trans.ValueKind==JsonValueKind.String)
                        sb.Append(trans.GetString());
            }
            else if(root.ValueKind==JsonValueKind.Array&&root.GetArrayLength()>0&&root[0].ValueKind==JsonValueKind.Array)
                foreach(var part in root[0].EnumerateArray())
                {
                    if(part.ValueKind!=JsonValueKind.Array||part.GetArrayLength()==0)continue;
                    if(part[0].ValueKind==JsonValueKind.String)sb.Append(part[0].GetString());
                }
            else throw FormatChanged();
            if(sb.Length==0)throw FormatChanged();
            return sb.ToString();
        }
        catch(JsonException ex)
        {
            throw Failure(TranslationFailureKind.InvalidResponse,"Google 网站通道的返回格式已经变化，当前版本无法解析。请手动切换翻译 Provider，并等待 Gtools 更新。",inner:ex);
        }
    }

    private static TranslationProviderException FormatChanged() => Failure(TranslationFailureKind.InvalidResponse,
        "Google 网站通道的返回格式已经变化，当前版本无法解析。请手动切换翻译 Provider，并等待 Gtools 更新。");

    private static TranslationFailureKind KindOf(HttpStatusCode status)=>status switch
    {
        HttpStatusCode.Forbidden=>TranslationFailureKind.Forbidden,
        HttpStatusCode.TooManyRequests=>TranslationFailureKind.TooManyRequests,
        >=HttpStatusCode.InternalServerError=>TranslationFailureKind.Server,
        _=>TranslationFailureKind.Other
    };

    private static TranslationProviderException Failure(TranslationFailureKind kind,string message,HttpStatusCode? status=null,
        DateTimeOffset? retryAfter=null,Exception? inner=null)=>new("google-web",kind,message,status,retryAfter,inner);

    private TranslationProviderException RateLimited(DateTimeOffset until,HttpStatusCode? status)
    {
        var seconds=Math.Max(1,(int)Math.Ceiling((until-_utcNow()).TotalSeconds));
        return Failure(TranslationFailureKind.TooManyRequests,
            $"当前代理出口被 Google 网站通道限流（HTTP 429），约 {seconds} 秒后可重试；也可以切换代理节点后立即使用“测试 Google 通道”。",
            status??HttpStatusCode.TooManyRequests,until);
    }

    private static DateTimeOffset ResolveRetryAfter(HttpResponseMessage response,DateTimeOffset now)
    {
        var value=response.Headers.RetryAfter;
        if(value?.Date is {} date&&date>now)return date;
        if(value?.Delta is {} delta&&delta>TimeSpan.Zero)return now+delta;
        return now+DefaultRateLimitCooldown;
    }

    private static string NormalizeLanguage(string language)=>string.IsNullOrWhiteSpace(language)?"auto":language.ToLowerInvariant();

    private void RemoveExpiredCacheEntries(DateTimeOffset now)
    {
        foreach(var key in _cache.Where(x=>x.Value.ExpiresAt<=now).Select(x=>x.Key).ToArray())_cache.Remove(key);
    }

    private void TrimCache()
    {
        while(_cache.Count>CacheCapacity)
        {
            var oldest=_cache.MinBy(x=>x.Value.LastAccess).Key;_cache.Remove(oldest);
        }
    }

    public void Dispose()
    {
        if(_disposed)return;_disposed=true;_lifetime.Cancel();
        lock(_stateGate){_cache.Clear();_inflight.Clear();}
        _lifetime.Dispose();if(_ownsHttp)_http.Dispose();
    }

    private readonly record struct RequestKey(string Text,string SourceLanguage,string TargetLanguage);
    private sealed record CacheEntry(TranslationResult Result,DateTimeOffset ExpiresAt,DateTimeOffset LastAccess);
}

public sealed class YoudaoWebTranslationProvider : ITranslationProvider
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    public string Id => "youdao-web";
    public string DisplayName => "有道免费网页通道（非官方）";

    // 有道用自己的语言代码；auto 让它自行判断。
    private static string Map(string language) => language switch
    {
        "zh-CN" or "zh" or "zh-Hans" => "zh-CHS",
        "auto" or "" => "auto",
        _ => language.Split('-')[0]
    };

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token = default)
    {
        var sw = Stopwatch.StartNew();
        var url = "https://aidemo.youdao.com/trans" +
                  $"?from={Uri.EscapeDataString(Map(request.SourceLanguage))}&to={Uri.EscapeDataString(Map(request.TargetLanguage))}" +
                  $"&q={Uri.EscapeDataString(request.Text)}";
        using var response = await _http.GetAsync(url, token); response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (doc.RootElement.TryGetProperty("errorCode", out var code) && code.ValueKind == JsonValueKind.String && code.GetString() is not ("0" or null))
            throw new InvalidOperationException($"有道通道返回错误码 {code.GetString()}。");
        if (!doc.RootElement.TryGetProperty("translation", out var translation) || translation.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("有道通道没有返回译文。");
        var text = string.Join(Environment.NewLine, translation.EnumerateArray().Select(x => x.GetString()).Where(x => x is not null));
        return new(DisplayName, text, sw.Elapsed, true);
    }
}
