using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gtools.Core;

namespace Gtools.App;

/// <summary>
/// 任何兼容 OpenAI Chat Completions 的接口（DeepSeek、OpenAI、本机模型服务等）。
/// 整个程序共用一个实例：复用连接，免去每次翻译重新握手。
/// </summary>
internal sealed partial class OpenAiTranslationProvider : ITranslationProvider,IDisposable
{
    public const string ProviderId="openai-compatible";
    public const string DeepSeekBaseUrl="https://api.deepseek.com";
    public const string DeepSeekModel="deepseek-flash";
    private readonly GtoolsSettings _settings;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<string,string> _unprotect;

    public OpenAiTranslationProvider(GtoolsSettings settings,HttpClient? httpClient=null,Func<string,string>? unprotect=null)
    {
        _settings=settings;_unprotect=unprotect??SecretProtector.Unprotect;
        if(httpClient is not null){_http=httpClient;return;}
        _http=new HttpClient{Timeout=TimeSpan.FromSeconds(45)};_ownsHttp=true;
    }

    public string Id => ProviderId;
    public string DisplayName => string.IsNullOrWhiteSpace(_settings.OpenAiModel)?"AI 接口":$"AI 接口 · {_settings.OpenAiModel.Trim()}";

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token = default)
    {
        var key=_unprotect(_settings.OpenAiKeyProtected);
        if(string.IsNullOrWhiteSpace(key))throw Failure(TranslationFailureKind.Other,"还没有填写 AI 接口的 API Key。请在设置的“翻译”部分填写并保存。");
        var model=_settings.OpenAiModel?.Trim()??"";
        if(model.Length==0)throw Failure(TranslationFailureKind.Other,"还没有填写 AI 接口的模型名。请在设置的“翻译”部分填写并保存。");
        if(!Uri.TryCreate((_settings.OpenAiBaseUrl??"").Trim().TrimEnd('/')+"/chat/completions",UriKind.Absolute,out var endpoint)||endpoint.Scheme is not("https" or "http"))
            throw Failure(TranslationFailureKind.Other,$"AI 接口地址格式不正确，应类似 {DeepSeekBaseUrl} 。");

        using var message=new HttpRequestMessage(HttpMethod.Post,endpoint);
        message.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);
        message.Content=new StringContent(JsonSerializer.Serialize(BuildBody(endpoint,model,request,_settings.OpenAiPrompt)),Encoding.UTF8,"application/json");
        var sw=Stopwatch.StartNew();string json;
        try
        {
            using var response=await _http.SendAsync(message,token);
            json=await response.Content.ReadAsStringAsync(token);
            if(!response.IsSuccessStatusCode)throw Failure(KindOf(response.StatusCode),DescribeStatus(response.StatusCode,json),response.StatusCode);
        }
        catch(OperationCanceledException ex) when(!token.IsCancellationRequested)
        { throw Failure(TranslationFailureKind.Timeout,"AI 接口请求超时，请检查网络后重试。",inner:ex); }
        catch(HttpRequestException ex)
        { throw Failure(TranslationFailureKind.Network,"无法连接 AI 接口，请检查接口地址和网络。",inner:ex); }
        return new(DisplayName,ParseContent(json),sw.Elapsed);
    }

    internal static Dictionary<string,object?> BuildBody(Uri endpoint,string model,TranslationRequest request,string defaultPrompt)
    {
        var body=new Dictionary<string,object?>
        {
            ["model"]=model,["temperature"]=0.1,["stream"]=false,
            ["messages"]=new object[]
            {
                new{role="system",content=string.IsNullOrWhiteSpace(request.Prompt)?defaultPrompt:request.Prompt},
                new{role="user",content=$"{request.SourceLanguage} -> {request.TargetLanguage}\n\n{request.Text}"}
            }
        };
        // DeepSeek 默认开启“思考模式”，先推理再作答；翻译一句话用不上，只会慢好几倍。
        // 这是 DeepSeek 专有参数，别的服务可能因为不认识而拒绝请求，所以只对它发送。
        if(endpoint.Host.EndsWith("deepseek.com",StringComparison.OrdinalIgnoreCase))body["thinking"]=new{type="disabled"};
        return body;
    }

    internal static string ParseContent(string json)
    {
        string? text=null;
        try
        {
            using var doc=JsonDocument.Parse(json);
            if(doc.RootElement.ValueKind==JsonValueKind.Object&&doc.RootElement.TryGetProperty("choices",out var choices)&&choices.ValueKind==JsonValueKind.Array
               &&choices.GetArrayLength()>0&&choices[0].ValueKind==JsonValueKind.Object&&choices[0].TryGetProperty("message",out var reply)
               &&reply.ValueKind==JsonValueKind.Object&&reply.TryGetProperty("content",out var content)&&content.ValueKind==JsonValueKind.String)
                text=content.GetString();
        }
        catch(JsonException ex){throw Failure(TranslationFailureKind.InvalidResponse,"AI 接口返回了无法解析的内容，请检查接口地址是否正确。",inner:ex);}
        if(text is null)throw Failure(TranslationFailureKind.InvalidResponse,"AI 接口的返回里没有译文，请检查接口地址和模型名。");
        // 个别推理模型把思考过程用 <think> 标签混在正文前面，不属于译文。
        text=ThinkBlock().Replace(text,"").Trim();
        if(text.Length==0)throw Failure(TranslationFailureKind.InvalidResponse,"AI 接口返回了空译文，请重试。");
        return text;
    }

    [GeneratedRegex(@"^\s*<think>.*?</think>",RegexOptions.Singleline)]
    private static partial Regex ThinkBlock();

    internal static string DescribeStatus(HttpStatusCode status,string body)
    {
        var code=(int)status;
        var summary=status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden=>$"AI 接口拒绝了这个 API Key（HTTP {code}）：Key 无效、已失效或没有权限。请在设置里重新填写。",
            HttpStatusCode.PaymentRequired=>$"AI 接口账户余额不足（HTTP {code}），请充值后重试。",
            HttpStatusCode.NotFound=>$"AI 接口地址或模型名不正确（HTTP {code}），请检查设置里的地址和模型。",
            HttpStatusCode.TooManyRequests=>$"AI 接口请求过于频繁或额度已用完（HTTP {code}），请稍后重试。",
            >=HttpStatusCode.InternalServerError=>$"AI 接口服务暂时异常（HTTP {code}），请稍后重试。",
            _=>$"AI 接口没有接受这次请求（HTTP {code}），常见原因是模型名不正确。"
        };
        var detail=ServerMessage(body);
        return detail.Length==0?summary:$"{summary}\n接口原话：{detail}";
    }

    /// <summary>取出服务端自己的错误说明。DeepSeek 认证失败时返回的是纯文本而不是 JSON，两种都要认。</summary>
    private static string ServerMessage(string body)
    {
        var text=body.Trim();
        try
        {
            using var doc=JsonDocument.Parse(text);
            if(doc.RootElement.ValueKind==JsonValueKind.Object&&doc.RootElement.TryGetProperty("error",out var error))
            {
                if(error.ValueKind==JsonValueKind.String)text=error.GetString()??"";
                else if(error.ValueKind==JsonValueKind.Object&&error.TryGetProperty("message",out var detail)&&detail.ValueKind==JsonValueKind.String)text=detail.GetString()??"";
                else text="";
            }
            else text="";
        }
        catch(JsonException){if(text.StartsWith('<'))text="";}
        text=text.ReplaceLineEndings(" ").Trim();
        return text.Length>200?text[..200]+"…":text;
    }

    private static TranslationFailureKind KindOf(HttpStatusCode status)=>status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden=>TranslationFailureKind.Forbidden,
        HttpStatusCode.TooManyRequests=>TranslationFailureKind.TooManyRequests,
        >=HttpStatusCode.InternalServerError=>TranslationFailureKind.Server,
        _=>TranslationFailureKind.Other
    };

    private static TranslationProviderException Failure(TranslationFailureKind kind,string message,HttpStatusCode? status=null,Exception? inner=null)
        =>new(ProviderId,kind,message,status,innerException:inner);

    public void Dispose(){if(_ownsHttp)_http.Dispose();}
}

/// <summary>
/// 决定一次翻译走哪个通道：默认通道失败时自动换到备用通道，窗口里明确点选的通道则只走那一个。
/// </summary>
internal sealed class TranslationRouter(GtoolsSettings settings,Func<string,ITranslationProvider> resolve,
    Func<TranslationRequest,CancellationToken,Task<TranslationResult>> googleAfterNetworkChange)
{
    public const string Default="default",GoogleRecheck="google-web-recheck";
    private const string Google="google-web",Youdao="youdao-web";

    public bool HasApiKey=>!string.IsNullOrWhiteSpace(settings.OpenAiKeyProtected);

    public async Task<TranslationResult> TranslateAsync(string text,string sourceLanguage,string targetLanguage,string mode,CancellationToken token=default)
    {
        var request=new TranslationRequest(text,sourceLanguage,targetLanguage,settings.OpenAiPrompt);
        if(mode==GoogleRecheck)return await googleAfterNetworkChange(request,token);
        if(!string.IsNullOrWhiteSpace(mode)&&mode!=Default)return await resolve(mode).TranslateAsync(request,token);

        var primaryId=Normalize(settings.TranslationProvider);
        try{return await resolve(primaryId).TranslateAsync(request,token);}
        // 取消和“没有可翻译的文字”换通道也一样，不触发备用。
        catch(Exception primary) when(primary is not(OperationCanceledException or ArgumentException)&&FallbackFor(primaryId) is {} backupId)
        {
            DiagnosticLog.Write("translation","fallback.started",$"{primaryId} -> {backupId}","Warning");
            TranslationResult result;
            try{result=await resolve(backupId).TranslateAsync(request,token);}
            catch(Exception backup) when(backup is not OperationCanceledException){throw Combine(primary,backup,backupId);}
            return result with{Note=$"{ShortName(primaryId)}{Reason(primary)}，已自动改用备用通道"};
        }
    }

    /// <summary>
    /// 备用关系：Google ↔ AI 接口。AI 接口必须已经填过 Key（这是用户自己选定的服务）；
    /// 有道从不作为自动备用，文字不会在用户没点的情况下发给它。
    /// </summary>
    internal string? FallbackFor(string primaryId)=>!settings.TranslationAutoFallback?null:primaryId switch
    {
        OpenAiTranslationProvider.ProviderId or Youdao=>Google,
        _=>HasApiKey?OpenAiTranslationProvider.ProviderId:null
    };

    private static string Normalize(string? id)=>id is OpenAiTranslationProvider.ProviderId or Youdao?id:Google;
    private static string ShortName(string id)=>id switch{OpenAiTranslationProvider.ProviderId=>"AI 接口",Youdao=>"有道",_=>"Google "};
    private static string Reason(Exception error)=>error is TranslationProviderException failure?failure.Kind switch
    {
        TranslationFailureKind.TooManyRequests=>"被限流",TranslationFailureKind.Timeout=>"超时",TranslationFailureKind.Network=>"无法连接",
        TranslationFailureKind.Forbidden=>"拒绝访问",TranslationFailureKind.Server=>"服务异常",_=>"失败"
    }:"失败";

    /// <summary>两个通道都失败时，保留默认通道的错误类型（窗口据此给出恢复按钮），并把备用通道的原因附在后面。</summary>
    private static Exception Combine(Exception primary,Exception backup,string backupId)
    {
        var message=$"{primary.Message}\n备用通道（{ShortName(backupId).Trim()}）也失败：{backup.Message}";
        return primary is TranslationProviderException failure
            ?new TranslationProviderException(failure.ProviderId,failure.Kind,message,failure.StatusCode,failure.RetryAfter,primary)
            :new InvalidOperationException(message,primary);
    }
}

internal static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Gtools.OpenAI.Key.v1");
    public static string Protect(string value) => string.IsNullOrEmpty(value) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
    public static string Unprotect(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
}
