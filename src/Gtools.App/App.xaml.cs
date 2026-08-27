using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Gtools.Core;
using Microsoft.Win32;

namespace Gtools.App;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private SettingsStore _settings=new();
    private LocalDatabase _db=null!;
    private GlobalHotkeyService? _hotkeys;
    private ClipboardHistoryService? _clipboard;
    private System.Windows.Forms.NotifyIcon? _tray;
    private DashboardWindow? _dashboard;
    private ClipboardWindow? _clipboardWindow;
    private FloatingDockWindow? _dock;
    private bool _exiting;
    private AppRuntimeOptions _runtime=new(null,false,false,false,false,null,null);
    private readonly DesktopModuleRegistry _modules=new();
    private readonly OcrWorkerClient _ocr=new();
    private readonly LongShotWorkerClient _longshotWorker=new();
    private readonly LongShotSessionGate _longshotSession=new();
    private readonly DictionaryService _dictionary=new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            _runtime=AppRuntimeOptions.Parse(e.Args);
            var root=_runtime.DataRoot;
            if(_runtime.SelfTest&&string.IsNullOrWhiteSpace(root))root=Path.Combine(Path.GetTempPath(),"Gtools-Acceptance",Guid.NewGuid().ToString("N"));
            AppPaths.Initialize(root,_runtime.IsTestProfile||_runtime.SelfTest);
            DiagnosticLog.Initialize(_runtime.Diagnostics||_runtime.IsTestProfile||_runtime.SelfTest);
            var mutexSuffix=_runtime.IsTestProfile||_runtime.SelfTest?Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.Root)))[..12]:"Product";
            _mutex=new Mutex(true,$"Local\\Gtools.{mutexSuffix}.Singleton",out var first);
            if(!first){MessageBox.Show("这个 Gtools 配置已在运行。","Gtools");Shutdown();return;}
            DiagnosticLog.Write("app","startup","Gtools 启动",data:new Dictionary<string,object?>{{"root",AppPaths.Root},{"testProfile",_runtime.IsTestProfile},{"selfTest",_runtime.SelfTest}});
            await _settings.LoadAsync();
            if(_runtime.CaptureProfile is not null)_settings.Current.LongShotProfile=_runtime.CaptureProfile.Value;
            _db=new LocalDatabase();_db.Initialize();
            _hotkeys=new GlobalHotkeyService();
            _clipboard=new ClipboardHistoryService(_db,()=>_settings.Current.ClipboardLimit){Paused=!_settings.Current.ClipboardEnabled};
            ConfigureModules();
            if(_runtime.SelfTest)
            {
                var report=await new AcceptanceRunner(_db,_modules,_ocr).RunAsync(_runtime.ReportPath);
                DiagnosticLog.Write("acceptance","completed",report.Passed?"自动验收通过":"自动验收失败",report.Passed?"Info":"Error");
                _exiting=true;await _modules.DisposeAsync();_clipboard.Dispose();_hotkeys.Dispose();ReleaseMutex();Shutdown(report.Passed?0:1);return;
            }
            RegisterHotkeys();CreateTray();ApplyStartup();await ApplyModulesAsync();
            if(_runtime.ShowDashboard)ShowDashboard();
        }
        catch(Exception ex){DiagnosticLog.Write("app","startup.failed",ex.ToString(),"Fatal");MessageBox.Show(ex.ToString(),"Gtools 启动失败");Shutdown(1);}
    }

    private void ConfigureModules()
    {
        _modules.Register(new DelegateDesktopModule("ocr","OCR",()=>_settings.Current.OcrEnabled,_=>Task.CompletedTask,_=>Task.CompletedTask,_=>WorkerHealth("Gtools.OcrWorker","按需启动，本地模型保持冷加载")));
        _modules.Register(new DelegateDesktopModule("translation","截图翻译",()=>_settings.Current.TranslationEnabled,_=>Task.CompletedTask,_=>Task.CompletedTask,_=>Task.FromResult(new ModuleStatus(ModuleHealth.Healthy,$"Provider: {_settings.Current.TranslationProvider}",DateTimeOffset.Now))));
        _modules.Register(new DelegateDesktopModule("vocabulary","英语生词本",()=>true,_=>Task.CompletedTask,_=>Task.CompletedTask,_=>Task.FromResult(new ModuleStatus(File.Exists(AppPaths.DatabaseFile)?ModuleHealth.Healthy:ModuleHealth.Degraded,"SQLite 本机存储",DateTimeOffset.Now))));
        _modules.Register(new DelegateDesktopModule("longshot","聊天长截图",()=>_settings.Current.LongShotEnabled,_=>Task.CompletedTask,_=>Task.CompletedTask,_=>WorkerHealth("Gtools.LongShotWorker",$"{_settings.Current.LongShotProfile} · 按需启动")));
        _modules.Register(new DelegateDesktopModule("clipboard","历史剪贴板",()=>_settings.Current.ClipboardEnabled,_=>{_clipboard!.Paused=false;return Task.CompletedTask;},_=>{_clipboard!.Paused=true;return Task.CompletedTask;},_=>Task.FromResult(new ModuleStatus(_clipboard!.Paused?ModuleHealth.Degraded:ModuleHealth.Healthy,$"SQLite {_db.DatabaseSizeBytes/1024d/1024:0.0} MB",DateTimeOffset.Now))));
        // 流量条和待办是两个独立模块，但共用同一个悬浮窗：各自控制自己那一块的显隐，都关掉窗口才整个隐藏。
        _modules.Register(new DelegateDesktopModule("monitor","性能悬浮条",()=>_settings.Current.MonitorEnabled,
            _=>{Dock().ShowMonitor(true);return Task.CompletedTask;},
            _=>{_dock?.ShowMonitor(false);return Task.CompletedTask;},
            _=>Task.FromResult(new ModuleStatus(_settings.Current.MonitorEnabled?ModuleHealth.Healthy:ModuleHealth.Degraded,"每秒采样；隐藏时停止",DateTimeOffset.Now))));
        _modules.Register(new DelegateDesktopModule("todo","待办清单",()=>_settings.Current.TodoEnabled,
            _=>{Dock().ShowTodo(true);return Task.CompletedTask;},
            _=>{_dock?.ShowTodo(false);return Task.CompletedTask;},
            _=>Task.FromResult(new ModuleStatus(_settings.Current.TodoEnabled?ModuleHealth.Healthy:ModuleHealth.Degraded,
                _settings.Current.TodoExpanded?"随悬浮条常驻显示":"已折叠，点流量条右侧箭头展开",DateTimeOffset.Now))));
    }
    private FloatingDockWindow Dock()=>_dock??=new FloatingDockWindow(_settings.Current,_settings,_db);
    private static Task<ModuleStatus> WorkerHealth(string worker,string detail)
    {
        try{var path=WorkerLocator.Find(worker);return Task.FromResult(new ModuleStatus(ModuleHealth.Healthy,$"{detail} · {Path.GetFileName(path)}",DateTimeOffset.Now));}
        catch(Exception ex){return Task.FromResult(new ModuleStatus(ModuleHealth.Failed,ex.Message,DateTimeOffset.Now));}
    }

    private void RegisterHotkeys()
    {
        Register(_settings.Current.OcrHotkey,()=>{if(_settings.Current.OcrEnabled)_=RunOcrAsync(false);});
        Register(_settings.Current.TranslateHotkey,()=>{if(_settings.Current.TranslationEnabled)_=RunOcrAsync(true);});
        Register(_settings.Current.LongShotHotkey,()=>{if(_settings.Current.LongShotEnabled)_=RunLongShotAsync();});
        Register(_settings.Current.ClipboardHotkey,()=>{if(_settings.Current.ClipboardEnabled)ShowClipboard();});
    }
    private void Register(string key,Action action){if(!_hotkeys!.Register(key,action,out var error)&&error is not null)ShowToast(error);}
    private void CreateTray()
    {
        var iconPath=Path.Combine(AppContext.BaseDirectory,"Assets","Gtools.ico");
        _tray=new System.Windows.Forms.NotifyIcon{Icon=new System.Drawing.Icon(iconPath),Text="Gtools",Visible=true};
        var menu=new System.Windows.Forms.ContextMenuStrip();
        Add(menu,"打开 Gtools",ShowDashboard);Add(menu,"截图 OCR  (Alt+B)",()=>_=RunOcrAsync(false));Add(menu,"截图翻译  (Alt+S)",()=>_=RunOcrAsync(true));Add(menu,"聊天长截图  (Alt+Shift+S)",()=>_=RunLongShotAsync());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());Add(menu,"历史剪贴板  (Ctrl+~)",ShowClipboard);Add(menu,"英语生词本",ShowVocabulary);Add(menu,"显示 / 隐藏待办",ToggleTodo);Add(menu,"模块健康状态",()=>_=ShowHealthAsync());
        if(_runtime.Diagnostics)Add(menu,"桌面测试工具",ShowDiagnostics);
        Add(menu,"设置",ShowSettings);menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());Add(menu,"退出",ExitApplication);
        _tray.ContextMenuStrip=menu;_tray.DoubleClick+=(_,_)=>Dispatcher.Invoke(ShowDashboard);
    }
    private static void Add(System.Windows.Forms.ContextMenuStrip menu,string text,Action action){var i=menu.Items.Add(text);i.Click+=(_,_)=>System.Windows.Application.Current.Dispatcher.Invoke(action);}

    private async Task RunOcrAsync(bool translate)
    {
        try
        {
            var png=await ScreenCaptureService.SelectAndCaptureAsync();if(png is null)return;ShowToast("正在准备本地 OCR…首次使用可能需要更久");
            var result=await _ocr.RecognizeAsync(png);if(!result.Success)throw new InvalidOperationException(result.Error??"OCR 失败");
            if(!translate){if(!string.IsNullOrWhiteSpace(result.Text))Clipboard.SetText(result.Text);ShowToast("OCR 已复制，点击校对",()=>new OcrEditorWindow(result.Text).Show());return;}
            if(string.IsNullOrWhiteSpace(result.Text))throw new InvalidOperationException("没有识别到文字。");
            var (sl,tl)=LanguageDetector.DetectPair(result.Text);var translated=await TranslateAsync(result.Text,sl,tl,"default");new TranslationWindow(result.Text,translated,sl,tl,TranslateAsync,CompareProvidersAsync,SaveWord).Show();
        }
        catch(Exception ex){DiagnosticLog.Write("ocr",translate?"translate.failed":"recognize.failed",ex.Message,"Error");ShowToast("操作失败："+ex.Message,()=>MessageBox.Show(ex.ToString(),"Gtools"));}
    }
    private ITranslationProvider CreateProvider(string id)=>id switch
    {
        "openai-compatible"=>new OpenAiTranslationProvider(_settings.Current),
        "youdao-web"=>new YoudaoWebTranslationProvider(),
        _=>new GoogleWebTranslationProvider()
    };
    private Task<TranslationResult> TranslateAsync(string text,string sl,string tl,string mode)
    {
        var id=mode=="default"||string.IsNullOrWhiteSpace(mode)?_settings.Current.TranslationProvider:mode;
        return CreateProvider(id).TranslateAsync(new TranslationRequest(text,sl,tl,_settings.Current.OpenAiPrompt));
    }
    /// <summary>免密钥通道全都跑；OpenAI 只有配置了 Key 才参与，避免每次对照都弹一条报错。</summary>
    private async Task<IReadOnlyList<ProviderOutcome>> CompareProvidersAsync(string text,string sl,string tl)
    {
        var ids=new List<string>{"google-web","youdao-web"};
        if(!string.IsNullOrWhiteSpace(_settings.Current.OpenAiKeyProtected))ids.Add("openai-compatible");
        var request=new TranslationRequest(text,sl,tl,_settings.Current.OpenAiPrompt);
        var tasks=ids.Select(async id=>
        {
            var provider=CreateProvider(id);
            try{var r=await provider.TranslateAsync(request);return new ProviderOutcome(r.Provider,r.Text,r.Elapsed,null);}
            catch(Exception ex){return new ProviderOutcome(provider.DisplayName,"",TimeSpan.Zero,ex.Message);}
        });
        return await Task.WhenAll(tasks);
    }
    private async void SaveWord(string word,string meaning,string context)
    {
        var trimmed=word.Trim();if(trimmed.Length>120)trimmed=trimmed[..120];
        var item=new VocabularyEntry(0,trimmed,meaning,context,"","",DateTimeOffset.Now);
        // 单个英文单词直接查词预填音标、释义、考试标签和例句；整句就保持原样。
        DictionaryLookup? prefill=null;
        if(_settings.Current.DictionaryLookupEnabled&&trimmed.Length>0&&trimmed.All(c=>char.IsLetter(c)||c=='-'||c=='\''))
        {
            try{var lookup=await _dictionary.LookupAsync(trimmed);if(!lookup.IsEmpty)prefill=lookup;}
            catch(Exception ex){DiagnosticLog.Write("vocabulary","prefill.failed",ex.Message,"Warning");}
        }
        var dialog=new VocabularyEditWindow(item,_dictionary,prefill);
        if(dialog.ShowDialog()==true){_db.AddVocabulary(dialog.Word,dialog.Meaning,dialog.Context,dialog.Tags,dialog.Notes,dialog.Phonetic);ShowToast("已加入生词本");}
    }
    private async Task RunLongShotAsync()
    {
        if(!_longshotSession.TryEnter()){ShowToast("已有长截图任务正在进行，请先完成或取消当前任务。");return;}
        try
        {
            var r=await new LongShotService(_longshotWorker,_settings.Current).CaptureAsync();
            if(r is null)return;
            if(!r.Success)throw new InvalidOperationException(r.Error);
            new LongShotPreviewWindow(r,_longshotWorker).Show();
        }
        catch(OperationCanceledException){DiagnosticLog.Write("longshot","capture.cancelled","长截图已取消");}
        catch(Exception ex){DiagnosticLog.Write("longshot","capture.failed",ex.Message,"Error");ShowToast("长截图失败："+ex.Message,()=>MessageBox.Show(ex.ToString(),"Gtools"));}
        finally{_longshotSession.Exit();}
    }

    private void ShowDashboard(){_dashboard??=new DashboardWindow(_settings.Current,ShowSettings,ShowVocabulary,ShowClipboard,()=>_=RunOcrAsync(false),()=>_=RunOcrAsync(true),()=>_=RunLongShotAsync());_dashboard.Refresh(_settings.Current);_dashboard.Show();_dashboard.Activate();_=RefreshHealthAsync();}
    private void ShowVocabulary(){var w=new VocabularyWindow(_db,_dictionary);w.Show();w.Activate();}
    // 待办被折叠、流量条又关着的时候，浮窗整个是隐藏的，这里是唯一的找回入口。
    private async void ToggleTodo()
    {
        if(!_settings.Current.TodoEnabled){_settings.Current.TodoEnabled=true;_settings.Current.TodoExpanded=true;}
        else _settings.Current.TodoExpanded=!_settings.Current.TodoExpanded;
        await _settings.SaveAsync();await ApplyModulesAsync();Dock().ShowTodo(_settings.Current.TodoEnabled);
        _dashboard?.Refresh(_settings.Current);
    }
    private void ShowClipboard()
    {
        if(_clipboardWindow is {IsVisible:true}){_clipboardWindow.FocusSearch();return;}
        var previous=NativeMethods.GetForegroundWindow();var window=new ClipboardWindow(_db,_clipboard!,previous);_clipboardWindow=window;
        window.Closed+=(_,_)=>{if(ReferenceEquals(_clipboardWindow,window))_clipboardWindow=null;};window.Show();window.Activate();
    }
    private void ShowSettings(){var w=new SettingsWindow(_settings.Current,_settings);w.SettingsChanged+=async(_,_)=>{ApplyStartup();await ApplyModulesAsync();_dashboard?.Refresh(_settings.Current);await RefreshHealthAsync();};w.ShowDialog();}
    private void ShowDiagnostics(){new DiagnosticsWindow(AppPaths.Root,DiagnosticLog.CurrentPath,()=>_modules.GetHealthAsync()).Show();}
    private void ShowToast(string text,Action? click=null)=>Dispatcher.Invoke(()=>new ToastWindow(text,click).Show());
    private async Task ShowHealthAsync(){var statuses=await _modules.GetHealthAsync();var lines=_modules.Modules.Select(m=>$"{Icon(statuses[m.Id].Health)} {m.DisplayName}：{statuses[m.Id].Message}");MessageBox.Show(string.Join(Environment.NewLine,lines),"Gtools · 模块健康状态");}
    private async Task RefreshHealthAsync(){if(_dashboard is null)return;var s=await _modules.GetHealthAsync();_dashboard.SetHealth(_modules.Modules.Select(m=>(m.DisplayName,s[m.Id])).ToArray());}
    private static string Icon(ModuleHealth health)=>health switch{ModuleHealth.Healthy=>"●",ModuleHealth.Degraded=>"▲",ModuleHealth.Failed=>"×",_=>"○"};
    private void ApplyStartup()
    {
        if(_runtime.IsTestProfile||_runtime.SelfTest)return;
        using var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");if(_settings.Current.StartWithWindows)key.SetValue("Gtools",$"\"{Environment.ProcessPath}\" --startup");else key.DeleteValue("Gtools",false);
    }
    private async Task ApplyModulesAsync(){await _modules.SynchronizeAsync();}
    private async void ExitApplication()
    {
        if(_exiting)return;_exiting=true;try{await _modules.DisposeAsync();}catch{}if(_tray is not null){_tray.Visible=false;_tray.Dispose();}_clipboard?.Dispose();_hotkeys?.Dispose();_dock?.Close();ReleaseMutex();Shutdown();
    }
    private void ReleaseMutex(){try{_mutex?.ReleaseMutex();}catch{}finally{_mutex?.Dispose();_mutex=null;}}
    protected override void OnExit(ExitEventArgs e){try{if(_tray is not null)_tray.Visible=false;}catch{}DiagnosticLog.Write("app","exit","Gtools 已退出");ReleaseMutex();base.OnExit(e);}
}
