namespace Gtools.Core;

public static class LanguageDetector
{
    public static (string Source, string Target) DetectPair(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ("auto", "zh-CN");
        var letters = 0; var chinese = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var v = rune.Value;
            if (v is >= 0x4E00 and <= 0x9FFF) chinese++;
            else if ((v is >= 'A' and <= 'Z') || (v is >= 'a' and <= 'z')) letters++;
        }
        return chinese >= Math.Max(1, letters / 3) ? ("zh-CN", "en") : ("en", "zh-CN");
    }
}
