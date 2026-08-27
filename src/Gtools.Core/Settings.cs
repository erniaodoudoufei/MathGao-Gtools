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
}

public sealed class SettingsStore
{
    public GtoolsSettings Current { get; private set; } = new();
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureAll();
        if (!File.Exists(AppPaths.SettingsFile)) return;
        try
        {
            await using var stream = File.OpenRead(AppPaths.SettingsFile);
            Current = await JsonSerializer.DeserializeAsync<GtoolsSettings>(stream, GtoolsJson.Options, cancellationToken) ?? new();
        }
        catch { Current = new(); }
    }
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureAll();
        var temp = AppPaths.SettingsFile + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, Current, GtoolsJson.Options, cancellationToken);
        File.Move(temp, AppPaths.SettingsFile, true);
    }
}
