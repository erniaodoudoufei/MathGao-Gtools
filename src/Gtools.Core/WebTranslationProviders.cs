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
    // 两个入口背后是同一套翻译服务、同一种返回格式，但各自按出口 IP 单独限流：
    // 共享代理出口在其中一个入口被 429 时，另一个通常仍然可用。文字始终只发给 Google。
    internal static readonly string[] Hosts={"translate.google.com","translate.googleapis.com"};
    private const string EndpointPath="/translate_a/single";
    // 实测 GET 网址 12 KB 仍可用、19 KB 返回 HTTP 400（约 2000 个汉字）；代理和中间设备的常见上限是 8 KB，
    // 超过这个长度就把原文放进 POST 表单，实测 3600 个汉字正常返回。
    internal const int MaxGetUrlLength=6000;
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
    private readonly EndpointState[] _endpoints=Hosts.Select(host=>new EndpointState(host)).ToArray();
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
        // Google 正常 1–2 秒返回。单次等待缩到 10 秒，卡住的入口能更快让位给另一个入口。
        _http=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(10)};_ownsHttp=true;
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
        lock(_stateGate)foreach(var endpoint in _endpoints)endpoint.CooldownUntil=DateTimeOffset.MinValue;
        return TranslateAsync(request,token);
    }

    public async Task<GoogleChannelTestResult> TestConnectionAsync(CancellationToken token=default)
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        var sw=Stopwatch.StartNew();
        var working=new List<string>();var broken=new List<string>();TranslationProviderException? lastError=null;
        await _requestGate.WaitAsync(token);
        try
        {
            // 连接测试由用户明确触发，允许在冷却期内对每个入口各发送一次固定合成文字，便于切换代理节点后立即验证。
            foreach(var endpoint in _endpoints)
            {
                var one=Stopwatch.StartNew();
                try
                {
                    _=await SendOnceAsync(endpoint,new TranslationRequest(TestText,"en","zh-CN"),token);
                    // A successful explicit probe means the user has restored connectivity (often by changing
                    // the system/TUN exit). Do not keep an obsolete 429 cooldown after that confirmation.
                    lock(_stateGate)endpoint.CooldownUntil=DateTimeOffset.MinValue;
                    working.Add($"{endpoint.Host} {one.Elapsed.TotalSeconds:0.0}s");
                }
                catch(TranslationProviderException ex)
                {
                    lastError=ex;broken.Add($"{endpoint.Host}（{(ex.StatusCode is {} status?$"HTTP {(int)status}":FailureName(ex.Kind))}）");
                }
            }
        }
        finally{_requestGate.Release();}
        if(working.Count==0)
        {
            var remaining=CooldownRemaining();
            return new(false,lastError!.Message,lastError.StatusCode,lastError.Kind,sw.Elapsed,remaining>TimeSpan.Zero?remaining:null);
        }
        // 只要有一个入口可用，翻译就能自动走它；另一个入口的状态如实列出来。
        var message=broken.Count==0?$"Google 通道可用 · {string.Join(" · ",working)}"
            :$"Google 通道可用（自动走可用入口）· 正常：{string.Join("、",working)} · 暂不可用：{string.Join("、",broken)}";
        return new(true,message,HttpStatusCode.OK,null,sw.Elapsed,null);
    }

    /// <summary>距离至少有一个入口可用还要多久；只要有入口不在冷却期就是零。</summary>
    internal TimeSpan CooldownRemaining()
    {
        lock(_stateGate){var remaining=_endpoints.Min(e=>e.CooldownUntil)-_utcNow();return remaining>TimeSpan.Zero?remaining:TimeSpan.Zero;}
    }

    private static string FailureName(TranslationFailureKind kind)=>kind switch
    {
        TranslationFailureKind.Timeout=>"超时",TranslationFailureKind.Network=>"无法连接",TranslationFailureKind.InvalidResponse=>"返回格式异常",_=>"失败"
    };

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
            EndpointState[] available;DateTimeOffset earliest;var now=_utcNow();
            lock(_stateGate){available=_endpoints.Where(e=>e.CooldownUntil<=now).ToArray();earliest=_endpoints.Min(e=>e.CooldownUntil);}
            if(available.Length==0)throw RateLimited(earliest,null);

            // 每个可用入口各试一次，一次请求最多发两次网络调用，不会因为重试反过来加重限流。
            TranslationProviderException? last=null;
            for(var i=0;i<available.Length;i++)
            {
                try{return await SendOnceAsync(available[i],request,token);}
                catch(TranslationProviderException ex) when(CanTryAnotherEndpoint(ex.Kind))
                {
                    last=ex;var transient=IsTransient(ex.Kind);
                    if(available.Length==1)
                    {
                        // 另一个入口正在冷却、只剩这一个可用时，瞬时故障在原入口上再试一次。
                        if(!transient)break;
                        await _delay(RetryDelay,token);
                        return await SendOnceAsync(available[0],request,token);
                    }
                    if(transient&&i<available.Length-1)await _delay(RetryDelay,token);
                }
            }
            // 全部入口都被限流时给出统一的冷却提示；否则报告最后一个入口的具体原因。
            DateTimeOffset? allCoolingUntil=null;
            lock(_stateGate){now=_utcNow();if(_endpoints.All(e=>e.CooldownUntil>now))allCoolingUntil=_endpoints.Min(e=>e.CooldownUntil);}
            throw allCoolingUntil is {} until?RateLimited(until,HttpStatusCode.TooManyRequests):last!;
        }
        finally{_requestGate.Release();}
    }

    // 429 和 403 是针对“这个入口 + 这个出口 IP”的拒绝，换入口有意义、在原入口重试没有意义；
    // 格式变化和 4xx 参数错误换入口也一样，直接报告。
    private static bool CanTryAnotherEndpoint(TranslationFailureKind kind)=>kind is TranslationFailureKind.TooManyRequests
        or TranslationFailureKind.Forbidden or TranslationFailureKind.Server or TranslationFailureKind.Timeout or TranslationFailureKind.Network;
    private static bool IsTransient(TranslationFailureKind kind)=>kind is TranslationFailureKind.Server or TranslationFailureKind.Timeout or TranslationFailureKind.Network;

    private async Task WaitForRequestSlotAsync(EndpointState endpoint,CancellationToken token)
    {
        // 限流按入口计，节流也按入口计：切换到另一个入口不需要再等 900 毫秒。
        var wait=endpoint.NextRequestAt-_utcNow();
        if(wait>TimeSpan.Zero)await _delay(wait,token);
        endpoint.NextRequestAt=_utcNow()+RequestInterval;
    }

    private async Task<TranslationResult> SendOnceAsync(EndpointState endpoint,TranslationRequest request,CancellationToken token)
    {
        await WaitForRequestSlotAsync(endpoint,token);
        var sw=Stopwatch.StartNew();
        using var message=BuildMessage(endpoint.Host,request);
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
                    lock(_stateGate)endpoint.CooldownUntil=until;
                    throw RateLimited(until,response.StatusCode);
                }
                throw Failure(KindOf(response.StatusCode),DescribeStatus(response.StatusCode),response.StatusCode);
            }
            var payload=await response.Content.ReadAsStringAsync(token);
            return new(DisplayName,ParsePayload(payload),sw.Elapsed,true);
        }
    }

    internal static HttpRequestMessage BuildMessage(string host,TranslationRequest request)
    {
        var url=$"https://{host}{EndpointPath}?client=gtx&dt=t&dj=1&ie=UTF-8&oe=UTF-8"+
            $"&sl={Uri.EscapeDataString(NormalizeLanguage(request.SourceLanguage))}"+
            $"&tl={Uri.EscapeDataString(NormalizeLanguage(request.TargetLanguage))}";
        var query="&q="+Uri.EscapeDataString(request.Text);
        if(url.Length+query.Length<=MaxGetUrlLength)return new(HttpMethod.Get,url+query);
        return new(HttpMethod.Post,url){Content=new FormUrlEncodedContent(new[]{new KeyValuePair<string,string>("q",request.Text)})};
    }

    internal static string DescribeStatus(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Forbidden => "Google 网站通道拒绝访问（HTTP 403），请检查当前代理出口或稍后重试。",
        HttpStatusCode.TooManyRequests => "当前代理出口被 Google 网站通道限流（HTTP 429）。请等待冷却，或切换代理节点后重试。",
        >=HttpStatusCode.InternalServerError => $"Google 网站服务暂时异常（HTTP {(int)statusCode}），Gtools 已自动换入口重试过一次。",
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
    /// <summary>NextRequestAt 只在持有 _requestGate 时读写；CooldownUntil 只在 _stateGate 内读写。</summary>
    private sealed class EndpointState(string host)
    {
        public string Host{get;}=host;
        public DateTimeOffset NextRequestAt,CooldownUntil;
    }
}

public sealed class YoudaoWebTranslationProvider : ITranslationProvider
{
    private readonly HttpClient _http;
    public string Id => "youdao-web";
    public string DisplayName => "有道免费网页通道（非官方）";
    public YoudaoWebTranslationProvider(HttpClient? httpClient=null)=>_http=httpClient??new(){Timeout=TimeSpan.FromSeconds(15)};

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
        // 原文放在 POST 表单里：长文本不受网址长度限制，也不会出现在代理的访问日志网址里。
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://aidemo.youdao.com/trans")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            { ["from"] = Map(request.SourceLanguage), ["to"] = Map(request.TargetLanguage), ["q"] = request.Text })
        };
        string payload;
        try
        {
            using var response = await _http.SendAsync(message, token);
            if (!response.IsSuccessStatusCode)
                throw Failure((int)response.StatusCode >= 500 ? TranslationFailureKind.Server : TranslationFailureKind.Other,
                    $"有道通道请求失败（HTTP {(int)response.StatusCode}），请稍后重试或改用其他通道。", response.StatusCode);
            payload = await response.Content.ReadAsStringAsync(token);
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        { throw Failure(TranslationFailureKind.Timeout, "有道通道请求超时，请检查网络后重试。", inner: ex); }
        catch (HttpRequestException ex)
        { throw Failure(TranslationFailureKind.Network, "无法连接有道通道，请检查网络设置。", inner: ex); }
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("errorCode", out var code) && code.ValueKind == JsonValueKind.String && code.GetString() is not ("0" or null))
                throw Failure(TranslationFailureKind.Other, DescribeErrorCode(code.GetString()!));
            if (!doc.RootElement.TryGetProperty("translation", out var translation) || translation.ValueKind != JsonValueKind.Array)
                throw Failure(TranslationFailureKind.InvalidResponse, "有道通道没有返回译文，接口格式可能已经变化。");
            var text = string.Join(Environment.NewLine, translation.EnumerateArray().Select(x => x.GetString()).Where(x => x is not null));
            return new(DisplayName, text, sw.Elapsed, true);
        }
        catch (JsonException ex)
        { throw Failure(TranslationFailureKind.InvalidResponse, "有道通道返回了无法解析的内容，接口可能已经变化。", inner: ex); }
    }

    // 103 实测在 4000 多个字符时出现，GET 和 POST 都一样，是服务本身的长度上限。
    internal static string DescribeErrorCode(string code) => code switch
    {
        "103" => "文本超过了有道通道的长度上限（错误码 103），请分段翻译或改用其他通道。",
        "411" or "412" => $"有道通道访问过于频繁（错误码 {code}），请稍后重试或改用其他通道。",
        _ => $"有道通道返回错误码 {code}，请稍后重试或改用其他通道。"
    };

    private static TranslationProviderException Failure(TranslationFailureKind kind, string message, HttpStatusCode? status = null, Exception? inner = null)
        => new("youdao-web", kind, message, status, innerException: inner);
}
