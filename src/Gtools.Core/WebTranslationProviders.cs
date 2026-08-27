using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Gtools.Core;

public sealed class GoogleWebTranslationProvider : ITranslationProvider
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    public string Id => "google-web";
    public string DisplayName => "Google 免费网页通道（非官方）";
    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token = default)
    {
        var sw = Stopwatch.StartNew();
        var url = "https://translate.googleapis.com/translate_a/single?client=gtx&dt=t" +
                  $"&sl={Uri.EscapeDataString(request.SourceLanguage)}&tl={Uri.EscapeDataString(request.TargetLanguage)}&q={Uri.EscapeDataString(request.Text)}";
        using var response = await _http.GetAsync(url, token); response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var sb = new StringBuilder();
        foreach (var part in doc.RootElement[0].EnumerateArray()) if (part.ValueKind == JsonValueKind.Array && part.GetArrayLength() > 0) sb.Append(part[0].GetString());
        return new(DisplayName, sb.ToString(), sw.Elapsed, true);
    }
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
