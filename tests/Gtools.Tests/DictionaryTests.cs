using Gtools.Core;

namespace Gtools.Tests;

/// <summary>
/// 夹具是按解析器需要的公开字段结构手写的最小合成数据，不含词典服务的原始响应或词条内容。
/// </summary>
public sealed class DictionaryTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void ParseYoudao_ExtractsPhoneticsMeaningsExamTagsAndExamples()
    {
        var lookup = DictionaryService.ParseYoudao(Fixture("youdao-synthetic.json"), "synthetic-entry");

        Assert.NotNull(lookup);
        Assert.Contains("英 /", lookup!.Phonetic);
        Assert.Contains("美 /", lookup.Phonetic);
        Assert.Contains("合成名词释义", lookup.Meaning);
        // 释义按词性分行，名词和动词都要在。
        Assert.Contains("n.", lookup.Meaning);
        Assert.Contains("v.", lookup.Meaning);
        Assert.Contains("CET4", lookup.Tags);
        Assert.Contains("考研", lookup.Tags);
        Assert.NotEmpty(lookup.Examples);
        Assert.All(lookup.Examples, e => Assert.False(string.IsNullOrWhiteSpace(e.Source)));
        // 例句里的高亮标记必须去掉，否则会连 <b> 一起存进生词本。
        Assert.All(lookup.Examples, e => Assert.DoesNotContain('<', e.Source));
        Assert.False(lookup.IsEmpty);
    }

    [Fact]
    public void ParseFreeDictionary_ExtractsEnglishDefinitionsWithPartOfSpeech()
    {
        var lookup = DictionaryService.ParseFreeDictionary(Fixture("freedict-synthetic.json"), "synthetic-entry");

        Assert.NotNull(lookup);
        Assert.Equal("noun. An invented definition written only to exercise the parser.", lookup!.EnglishDefinition);
        Assert.Equal("/sɪnˈθetɪk/", lookup.Phonetic);
        Assert.Equal("", lookup.Meaning);
        Assert.False(lookup.IsEmpty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"ec\":{}}")]
    public void Parsers_ReturnNullInsteadOfThrowingOnEmptyOrUnexpectedPayloads(string? json)
    {
        Assert.Null(DictionaryService.ParseYoudao(json, "focus"));
        Assert.Null(DictionaryService.ParseFreeDictionary(json, "focus"));
    }

    [Fact]
    public void Parsers_ToleratePartialPayloads()
    {
        // 有道对生僻词只回音标不回释义；不能因此判成查不到。
        var phoneticOnly = DictionaryService.ParseYoudao("{\"ec\":{\"word\":[{\"usphone\":\"tɛst\"}]}}", "test");
        Assert.NotNull(phoneticOnly);
        Assert.Contains("美 /tɛst/", phoneticOnly!.Phonetic);
        Assert.Equal("", phoneticOnly.Meaning);

        // 没有 meanings 数组时不应崩，只要有音标就还算有效。
        var noMeanings = DictionaryService.ParseFreeDictionary("[{\"word\":\"test\",\"phonetic\":\"/tɛst/\"}]", "test");
        Assert.NotNull(noMeanings);
        Assert.Equal("/tɛst/", noMeanings!.Phonetic);
    }

    [Fact]
    public void LookupUrls_EscapeTheQuery()
    {
        Assert.Contains("q=take%20off", DictionaryService.YoudaoUrl("take off"));
        Assert.EndsWith("/take%20off", DictionaryService.FreeDictionaryUrl("take off"));
    }
}

public sealed class MonitorPlacementTests
{
    private static readonly DesktopRect Work = new(0, 0, 2560, 1400);

    [Fact]
    public void Resolve_FallsBackToBottomRightWhenNeverPlaced()
    {
        var (left, top) = MonitorPlacement.Resolve(double.NaN, double.NaN, Work, 170, 54);
        Assert.Equal(2560 - 170 - 14, left);
        Assert.Equal(1400 - 54 - 14, top);
    }

    [Fact]
    public void Resolve_KeepsAValidSavedPositionExactly()
    {
        Assert.Equal((300d, 900d), MonitorPlacement.Resolve(300, 900, Work, 170, 54));
    }

    [Theory]
    [InlineData(99999d, 99999d)]
    [InlineData(-5000d, -5000d)]
    public void Resolve_PullsOffScreenPositionsBackIntoView(double savedLeft, double savedTop)
    {
        var (left, top) = MonitorPlacement.Resolve(savedLeft, savedTop, Work, 170, 54);
        Assert.InRange(left, Work.Left, Work.Right - 170);
        Assert.InRange(top, Work.Top, Work.Bottom - 54);
    }

    [Fact]
    public void Resolve_TreatsAHalfWrittenPositionAsUnplaced()
    {
        // 只有一个坐标是 NaN 时不能直接用，否则窗口会跑到 (300, NaN)。
        var (left, top) = MonitorPlacement.Resolve(300, double.NaN, Work, 170, 54);
        Assert.Equal(2560 - 170 - 14, left);
        Assert.Equal(1400 - 54 - 14, top);
    }
}

public sealed class RateFormattingTests
{
    [Theory]
    [InlineData(0, "0.00 B/s")]
    [InlineData(512, "512 B/s")]
    [InlineData(10220, "9.98 KB/s")]
    [InlineData(12185, "11.9 KB/s")]
    [InlineData(153600, "150 KB/s")]
    [InlineData(1101004, "1.05 MB/s")]
    public void Rate_UsesThreeSignificantDigitsLikeTrafficMonitor(double bytesPerSecond, string expected)
    {
        Assert.Equal(expected, ByteRate.PerSecond(bytesPerSecond));
    }
}
