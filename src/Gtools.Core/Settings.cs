using System.Text.Json;

namespace Gtools.Core;

public sealed class GtoolsSettings
{
    public bool StartWithWindows { get; set; }
    public bool OcrEnabled { get; set; } = true;
    public bool TranslationEnabled { get; set; } = true;
    public bool VocabularyEnabled { get; set; } = true;
    public bool LongShotEnabled { get; set; } = true;
    public bool ClipboardEnabled { get; set; } = true;
    public bool MonitorEnabled { get; set; }
    public bool MonitorLocked { get; set; }
    public double MonitorLeft { get; set; } = double.NaN;
    public double MonitorTop { get; set; } = double.NaN;
    public int ClipboardLimit { get; set; } = 500;
    public string OcrHotkey { get; set; } = "Alt+B";
    public string TranslateHotkey { get; set; } = "Alt+S";
    public string LongShotHotkey { get; set; } = "Alt+Shift+S";
    public string ClipboardHotkey { get; set; } = "Ctrl+Oem3";
    public string TranslationProvider { get; set; } = "google-web";
    /// <summary>
    /// 默认通道失败时自动改用备用通道：Google 失败改用已填写 Key 的 AI 接口，AI 接口或有道失败改用 Google。
    /// 不会自动改用有道，那仍然需要在翻译窗口里手动点。
    /// </summary>
    public bool TranslationAutoFallback { get; set; } = true;
    public string OpenAiBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string OpenAiModel { get; set; } = "gpt-4.1-mini";
    public string OpenAiKeyProtected { get; set; } = "";
    public string OpenAiPrompt { get; set; } = "Translate accurately and preserve formatting. Return only the translation.";
    public int LongShotSegmentHeight { get; set; } = 8000;
    public LongShotCaptureProfile LongShotProfile { get; set; } = LongShotCaptureProfile.WeChat4;
    public bool LongShotScrollUp { get; set; } = true;
    public int LongShotMaxFrames { get; set; } = 60;
    public MonitorTheme MonitorTheme { get; set; } = MonitorTheme.Light;
    public bool DictionaryLookupEnabled { get; set; } = true;
    public bool TodoEnabled { get; set; } = true;
    public bool TodoExpanded { get; set; } = true;
    public int TodoVisibleRows { get; set; } = 6;
    public int[]? TodoColorGroupOrder { get; set; }
}

public sealed class SettingsStore(string? settingsFile = null)
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private string FilePath => settingsFile ?? AppPaths.SettingsFile;
    public GtoolsSettings Current { get; private set; } = new();
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (settingsFile is null) AppPaths.EnsureAll();
        if (!File.Exists(FilePath)) return;
        try
        {
            using var stream = File.OpenRead(FilePath);
            Current = await JsonSerializer.DeserializeAsync<GtoolsSettings>(stream, GtoolsJson.Options, cancellationToken).ConfigureAwait(false) ?? new();
        }
        catch { Current = new(); }
    }
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (settingsFile is null) AppPaths.EnsureAll();
            else Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            var temp = FilePath + ".tmp";
            using (var stream = File.Create(temp))
                await JsonSerializer.SerializeAsync(stream, Current, GtoolsJson.Options, cancellationToken).ConfigureAwait(false);
            File.Move(temp, FilePath, true);
        }
        finally { _saveGate.Release(); }
    }
}
