using Gtools.Core;

namespace Gtools.App;

internal sealed class DelegateDesktopModule(
    string id,string displayName,Func<bool> enabled,Func<CancellationToken,Task> start,Func<CancellationToken,Task> stop,
    Func<CancellationToken,Task<ModuleStatus>> health) : IDesktopModule
{
    private bool _started;
    public string Id=>id;public string DisplayName=>displayName;public bool IsEnabled=>enabled();
    public async Task StartAsync(CancellationToken token=default){if(_started)return;await start(token);_started=true;DiagnosticLog.Write(Id,"module.started",$"{DisplayName} 已启动");}
    public async Task StopAsync(CancellationToken token=default){if(!_started)return;await stop(token);_started=false;DiagnosticLog.Write(Id,"module.stopped",$"{DisplayName} 已停止");}
    public Task<ModuleStatus> GetHealthAsync(CancellationToken token=default)=>IsEnabled?health(token):Task.FromResult(new ModuleStatus(ModuleHealth.Disabled,"已关闭",DateTimeOffset.Now));
    public async ValueTask DisposeAsync(){await StopAsync();}
}

internal sealed class DesktopModuleRegistry : IAsyncDisposable
{
    private readonly List<IDesktopModule> _modules=[];
    public IReadOnlyList<IDesktopModule> Modules=>_modules;
    public void Register(IDesktopModule module){if(_modules.Any(x=>x.Id==module.Id))throw new InvalidOperationException($"模块重复：{module.Id}");_modules.Add(module);}
    public async Task SynchronizeAsync(CancellationToken token=default)
    {
        foreach(var m in _modules){try{if(m.IsEnabled)await m.StartAsync(token);else await m.StopAsync(token);}catch(Exception ex){DiagnosticLog.Write(m.Id,"module.sync.failed",ex.Message,"Error");}}
    }
    public async Task<IReadOnlyDictionary<string,ModuleStatus>> GetHealthAsync(CancellationToken token=default)
    {
        var result=new Dictionary<string,ModuleStatus>();foreach(var m in _modules)try{result[m.Id]=await m.GetHealthAsync(token);}catch(Exception ex){result[m.Id]=new(ModuleHealth.Failed,ex.Message,DateTimeOffset.Now);}return result;
    }
    public async ValueTask DisposeAsync(){foreach(var m in _modules.AsEnumerable().Reverse())await m.DisposeAsync();}
}
