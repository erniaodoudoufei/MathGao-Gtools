using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gtools.Core;

namespace Gtools.App;

internal sealed class OpenAiTranslationProvider(GtoolsSettings settings) : ITranslationProvider
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(45) };
    public string Id => "openai-compatible";
    public string DisplayName => "OpenAI 兼容接口";
    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken token = default)
    {
        var key = SecretProtector.Unprotect(settings.OpenAiKeyProtected);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("请先在设置中填写 OpenAI 兼容接口 Key。");
        var endpoint = settings.OpenAiBaseUrl.TrimEnd('/') + "/chat/completions";
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var body = new
        {
            model = settings.OpenAiModel,
            temperature = 0.1,
            messages = new object[]
            {
                new { role="system", content=request.Prompt ?? settings.OpenAiPrompt },
                new { role="user", content=$"{request.SourceLanguage} -> {request.TargetLanguage}\n\n{request.Text}" }
            }
        };
        message.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var sw = Stopwatch.StartNew(); using var response = await _http.SendAsync(message, token);
        var json = await response.Content.ReadAsStringAsync(token); response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(json);
        var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        return new(DisplayName, text.Trim(), sw.Elapsed);
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
