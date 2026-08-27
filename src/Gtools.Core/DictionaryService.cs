using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Gtools.Core;

/// <summary>
/// 英语生词查询。两个都是免密钥接口，并发查、结果合并：
/// 有道词典给中文释义、英美音标、考试标签和双语例句；dictionaryapi.dev 补一条英英释义。
/// 任何一路失败都不影响另一路，全失败才算查不到。
/// 解析与取数分开，解析部分是纯函数，便于用真实响应做回归。
/// </summary>
public sealed class DictionaryService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public static string YoudaoUrl(string word) => $"https://dict.youdao.com/jsonapi?q={Uri.EscapeDataString(word)}";
    public static string FreeDictionaryUrl(string word) => $"https://api.dictionaryapi.dev/api/v2/entries/en/{Uri.EscapeDataString(word)}";

    public async Task<DictionaryLookup> LookupAsync(string word, CancellationToken token = default)
    {
        var query = word.Trim();
        if (string.IsNullOrWhiteSpace(query)) return Empty(query);
        var youdao = SafeAsync(async () => ParseYoudao(await GetAsync(YoudaoUrl(query), token), query));
        var english = SafeAsync(async () => ParseFreeDictionary(await GetAsync(FreeDictionaryUrl(query), token), query));
        await Task.WhenAll(youdao, english);
        var a = youdao.Result; var b = english.Result;
        var sources = new List<string>();
        if (a is not null) sources.Add("有道词典");
        if (b is not null) sources.Add("dictionaryapi.dev");
        return new(
            query,
            !string.IsNullOrWhiteSpace(a?.Phonetic) ? a!.Phonetic : b?.Phonetic ?? "",
            a?.Meaning ?? "",
            a?.Tags ?? Array.Empty<string>(),
            (a?.Examples is { Length: > 0 } ? a.Examples : b?.Examples) ?? Array.Empty<DictionaryExample>(),
            b?.EnglishDefinition ?? "",
            sources.ToArray());
    }

    private static DictionaryLookup Empty(string word) =>
        new(word, "", "", Array.Empty<string>(), Array.Empty<DictionaryExample>(), "", Array.Empty<string>());

    private static async Task<string?> GetAsync(string url, CancellationToken token)
    {
        using var response = await Http.GetAsync(url, token);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(token) : null;
    }

    private static async Task<DictionaryLookup?> SafeAsync(Func<Task<DictionaryLookup?>> action)
    {
        try { return await action(); }
        catch (Exception ex) { DiagnosticLog.Write("dictionary", "lookup.failed", ex.Message, "Warning"); return null; }
    }

    /// <summary>有道 jsonapi：ec 是英汉词条，blng_sents_part 是双语例句，exam_type 是考试标签。</summary>
    public static DictionaryLookup? ParseYoudao(string? json, string word)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("ec", out var ec) || ec.ValueKind != JsonValueKind.Object) return null;

        var phonetic = ""; var meanings = new List<string>();
        if (ec.TryGetProperty("word", out var words) && words.ValueKind == JsonValueKind.Array && words.GetArrayLength() > 0)
        {
            var first = words[0];
            var uk = Text(first, "ukphone"); var us = Text(first, "usphone");
            phonetic = string.Join("  ", new[] { uk.Length > 0 ? $"英 /{uk}/" : "", us.Length > 0 ? $"美 /{us}/" : "" }.Where(x => x.Length > 0));
            if (first.TryGetProperty("trs", out var trs) && trs.ValueKind == JsonValueKind.Array)
                foreach (var group in trs.EnumerateArray())
                    if (group.TryGetProperty("tr", out var tr) && tr.ValueKind == JsonValueKind.Array)
                        foreach (var item in tr.EnumerateArray())
                            if (item.TryGetProperty("l", out var l) && l.TryGetProperty("i", out var i) && i.ValueKind == JsonValueKind.Array)
                                foreach (var line in i.EnumerateArray())
                                    if (line.ValueKind == JsonValueKind.String && line.GetString() is { } value && value.Trim().Length > 0)
                                        meanings.Add(value.Trim());
        }

        var tags = new List<string>();
        if (ec.TryGetProperty("exam_type", out var exams) && exams.ValueKind == JsonValueKind.Array)
            foreach (var exam in exams.EnumerateArray())
                if (exam.ValueKind == JsonValueKind.String && exam.GetString() is { Length: > 0 } value) tags.Add(value);

        var examples = new List<DictionaryExample>();
        if (root.TryGetProperty("blng_sents_part", out var sentences) && sentences.ValueKind == JsonValueKind.Object
            && sentences.TryGetProperty("sentence-pair", out var pairs) && pairs.ValueKind == JsonValueKind.Array)
            foreach (var pair in pairs.EnumerateArray().Take(3))
            {
                var source = Strip(Text(pair, "sentence")); var translation = Strip(Text(pair, "sentence-translation"));
                if (source.Length > 0) examples.Add(new(source, translation));
            }

        if (meanings.Count == 0 && examples.Count == 0 && phonetic.Length == 0) return null;
        return new(word, phonetic, string.Join("\n", meanings), tags.ToArray(), examples.ToArray(), "", Array.Empty<string>());
    }

    /// <summary>dictionaryapi.dev：英英释义与例句，用来补有道没有的部分。</summary>
    public static DictionaryLookup? ParseFreeDictionary(string? json, string word)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return null;
        var entry = doc.RootElement[0];

        var phonetic = Text(entry, "phonetic");
        if (phonetic.Length == 0 && entry.TryGetProperty("phonetics", out var phonetics) && phonetics.ValueKind == JsonValueKind.Array)
            foreach (var p in phonetics.EnumerateArray())
            {
                var value = Text(p, "text"); if (value.Length == 0) continue;
                phonetic = value; break;
            }

        var lines = new List<string>(); var examples = new List<DictionaryExample>();
        if (entry.TryGetProperty("meanings", out var meanings) && meanings.ValueKind == JsonValueKind.Array)
            foreach (var meaning in meanings.EnumerateArray().Take(3))
            {
                var pos = Text(meaning, "partOfSpeech");
                if (!meaning.TryGetProperty("definitions", out var definitions) || definitions.ValueKind != JsonValueKind.Array) continue;
                foreach (var definition in definitions.EnumerateArray().Take(2))
                {
                    var text = Text(definition, "definition"); if (text.Length == 0) continue;
                    lines.Add(pos.Length > 0 ? $"{pos}. {text}" : text);
                    var example = Text(definition, "example");
                    if (example.Length > 0 && examples.Count < 3) examples.Add(new(example, ""));
                }
            }
        if (lines.Count == 0 && phonetic.Length == 0) return null;
        return new(word, phonetic, "", Array.Empty<string>(), examples.ToArray(), string.Join("\n", lines), Array.Empty<string>());
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    // 有道例句会带 <b> 之类的高亮标记。
    private static string Strip(string value)
    {
        if (value.IndexOf('<') < 0) return value.Trim();
        var builder = new StringBuilder(value.Length); var inside = false;
        foreach (var c in value)
        {
            if (c == '<') inside = true;
            else if (c == '>') inside = false;
            else if (!inside) builder.Append(c);
        }
        return builder.ToString().Trim();
    }
}
