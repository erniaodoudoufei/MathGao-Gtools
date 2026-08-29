using System.Diagnostics;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Gtools.Core;

public sealed class GoogleWebTranslationProvider : ITranslationProvider
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly HttpClient _http;
    public string Id => "google-web";
    public string DisplayName => "Google 免费网页通道（非官方）";

    public GoogleWebTranslationProvider(HttpClient? httpClient=null) => _http=httpClient??SharedHttp;

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token = default)
    {
        var sw = Stopwatch.StartNew();
        var url = "https://translate.googleapis.com/translate_a/single?client=gtx&dt=t" +
                  $"&sl={Uri.EscapeDataString(request.SourceLanguage)}&tl={Uri.EscapeDataString(request.TargetLanguage)}&q={Uri.EscapeDataString(request.Text)}";
        HttpResponseMessage response;
        try { response=await _http.GetAsync(url,token); }
        catch(OperationCanceledException ex) when(!token.IsCancellationRequested)
        {
            throw new TimeoutException("Google 非官方翻译通道请求超时，请检查网络、代理或 Google 连通性后重试。",ex);
        }
        catch(HttpRequestException ex)
        {
            throw new InvalidOperationException("无法连接 Google 非官方翻译通道，请检查网络、DNS 或代理设置。",ex);
        }

        using(response)
        {
            if(!response.IsSuccessStatusCode)throw new InvalidOperationException(DescribeStatus(response.StatusCode));
            var payload=await response.Content.ReadAsStringAsync(token);
            return new(DisplayName,ParsePayload(payload),sw.Elapsed,true);
        }
    }

    internal static string DescribeStatus(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Forbidden => "Google 非官方翻译通道拒绝访问（HTTP 403），可能是地区网络限制或通道策略已经变化。请稍后重试，或在设置中切换翻译 Provider。",
        HttpStatusCode.TooManyRequests => "Google 非官方翻译通道请求过于频繁（HTTP 429），请稍后重试，或在设置中切换翻译 Provider。",
        >=HttpStatusCode.InternalServerError => $"Google 翻译服务暂时异常（HTTP {(int)statusCode}），请稍后重试。",
        _ => $"Google 非官方翻译通道请求失败（HTTP {(int)statusCode}），请检查网络或切换翻译 Provider。"
    };

    internal static string ParsePayload(string payload)
    {
        try
        {
            using var doc=JsonDocument.Parse(payload);
            var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Array||root.GetArrayLength()==0||root[0].ValueKind!=JsonValueKind.Array)
                throw FormatChanged();
            var sb=new StringBuilder();
            foreach(var part in root[0].EnumerateArray())
            {
                if(part.ValueKind!=JsonValueKind.Array||part.GetArrayLength()==0)continue;
                if(part[0].ValueKind==JsonValueKind.String)sb.Append(part[0].GetString());
            }
            if(sb.Length==0)throw FormatChanged();
            return sb.ToString();
        }
        catch(JsonException ex)
        {
            throw new InvalidOperationException("Google 非官方翻译通道的返回格式已经变化，当前版本无法解析。请切换翻译 Provider，并等待 Gtools 更新。",ex);
        }
    }

    private static InvalidOperationException FormatChanged() => new("Google 非官方翻译通道的返回格式已经变化，当前版本无法解析。请切换翻译 Provider，并等待 Gtools 更新。");
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
