using System.Text.Json.Serialization;

namespace Gtools.Core;

public enum ModuleHealth { Disabled, Healthy, Degraded, Failed }
public sealed record ModuleStatus(ModuleHealth Health, string Message, DateTimeOffset CheckedAt);

public interface IDesktopModule : IAsyncDisposable
{
    string Id { get; }
    string DisplayName { get; }
    bool IsEnabled { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<ModuleStatus> GetHealthAsync(CancellationToken cancellationToken = default);
}

public sealed record TranslationRequest(string Text, string SourceLanguage, string TargetLanguage, string? Prompt = null);
public sealed record TranslationResult(string Provider, string Text, TimeSpan Elapsed, bool IsUnofficial = false);

public interface ITranslationProvider
{
    string Id { get; }
    string DisplayName { get; }
    Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default);
}

public sealed record OcrPoint(float X, float Y);
public sealed record OcrRegion(string Text, float Confidence, OcrPoint[] Polygon);
public sealed record OcrRequest(string RequestId, string ImageBase64, string Language = "ch");
public sealed record OcrResponse(string RequestId, bool Success, string Text, OcrRegion[] Regions, string? Error = null);
public enum LongShotCaptureProfile { WeChat4, QQ9, Generic }
public enum MonitorTheme { Light, Dark }

public sealed record DictionaryExample(string Source, string Translation);
public sealed record DictionaryLookup(string Word, string Phonetic, string Meaning, string[] Tags,
    DictionaryExample[] Examples, string EnglishDefinition, string[] Sources)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Meaning) && string.IsNullOrWhiteSpace(EnglishDefinition);
}
public sealed record SeamDiagnostic(int Index,int Offset,double Confidence,bool UsedFallback,int[] RegionOffsets,
    int CutY=0,string Source="unconfirmed");
public sealed record LongShotDiagnostics(LongShotCaptureProfile Profile,int FrameCount,int Width,int Height,long OutputPixels,
    long PeakWorkingSetBytes,SeamDiagnostic[] Seams,string[] PauseReasons,DateTimeOffset CompletedAt);
public readonly record struct FrameAdvanceMeasurement(int SignedAdvance,double Confidence,bool IsReliable);
public readonly record struct LongShotOffsetHint(int Offset,double Confidence,bool IsReliable);
public sealed record LongShotRequest(string RequestId, string[] FramePaths, string OutputPath, int SegmentHeight,
    int MaxOutputPixels = 180_000_000, double MinimumConfidence = 0.62,LongShotCaptureProfile Profile=LongShotCaptureProfile.WeChat4,
    double EdgeExclusionRatio=.06,string? DiagnosticsPath=null,LongShotOffsetHint[]? OffsetHints=null,
    ConfirmedLongShotSeam[]? ConfirmedSeams=null);
public enum LongShotAlignmentSource { Suggested, ManualAnchors }
public sealed record ConfirmedLongShotSeam(string UpperFrame,string LowerFrame,int Offset,int CutY,
    LongShotAlignmentSource Source,double Confidence);
public sealed record LongShotPairRequest(string RequestId,string UpperFrame,string LowerFrame,string PreviewPath,
    int? ManualOffset=null,int? CutY=null);
public sealed record LongShotPairResponse(string RequestId,bool Success,bool CanConfirm,int Offset,int CutY,
    double Confidence,string Message,string? PreviewPath=null,string? OverlayPath=null,int Width=0,int Height=0,
    double ChangedRatio=0,string? Error=null);
public sealed record LongShotResponse(string RequestId, bool Success, string? OutputPath, string[] SegmentPaths,
    double[] SeamConfidences, string[] Warnings, string? Error = null,bool RequiresReview=false,LongShotDiagnostics? Diagnostics=null);
public sealed record LongShotCropRequest(string RequestId,string SourcePath,string OutputPath,int X,int Y,int Width,int Height,
    int MaxOutputPixels=180_000_000);
public sealed record LongShotCropResponse(string RequestId,bool Success,string? OutputPath,int Width,int Height,string? Error=null);

public static class GtoolsJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 枚举默认会被写成裸数字，settings.json 里看不懂也不好手改；
        // 转成字符串更可读，读取时仍然兼容旧文件里的数字。
        Converters = { new JsonStringEnumConverter() }
    };
}
