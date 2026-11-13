using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Gtools.Core;

namespace Gtools.App;

internal static class WorkerLocator
{
    public static string Find(string name)
    {
        var baseDir = AppContext.BaseDirectory;
        var direct = new[]
        {
            Path.Combine(baseDir,"Workers",name,name+".exe"), Path.Combine(baseDir,"Workers",name+".exe"), Path.Combine(baseDir,name+".exe")
        };
        foreach (var p in direct) if (File.Exists(p)) return p;
        var current = new DirectoryInfo(baseDir);
        while (current is not null)
        {
            var src = Path.Combine(current.FullName, "src", name, "bin");
            if (Directory.Exists(src))
            {
                var found = Directory.EnumerateFiles(src, name + ".exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (found is not null) return found;
            }
            current = current.Parent;
        }
        throw new FileNotFoundException($"找不到 {name}.exe。请运行 build.ps1 构建完整套件。");
    }
}

internal sealed class OcrWorkerClient
{
    public async Task<OcrResponse> RecognizeAsync(byte[] png, CancellationToken token = default)
    {
        var request = new OcrRequest(Guid.NewGuid().ToString("N"), Convert.ToBase64String(png));
        return await RunAsync<OcrRequest,OcrResponse>("Gtools.OcrWorker", request, TimeSpan.FromMinutes(5), token);
    }
    internal static async Task<TResponse> RunAsync<TRequest,TResponse>(string worker, TRequest request, TimeSpan timeout, CancellationToken token,
        IReadOnlyList<string>? workerArguments=null)
    {
        var pipeName = $"Gtools.{worker}.{Environment.ProcessId}.{Guid.NewGuid():N}";
        var workerPath=WorkerLocator.Find(worker);var psi=new ProcessStartInfo(workerPath)
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,WorkingDirectory=Path.GetDirectoryName(workerPath)! };
        psi.ArgumentList.Add("--pipe");psi.ArgumentList.Add(pipeName);psi.ArgumentList.Add("--data-root");psi.ArgumentList.Add(AppPaths.Root);if(DiagnosticLog.Enabled)psi.ArgumentList.Add("--diagnostics");
        if(workerArguments is not null)foreach(var argument in workerArguments)psi.ArgumentList.Add(argument);
        DiagnosticLog.Write(worker,"worker.start","启动隔离 Worker",requestId:pipeName,data:new Dictionary<string,object?>{{"path",workerPath}});
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"无法启动 {worker}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token); cts.CancelAfter(timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(15_000, cts.Token);
            await PipeJson.WriteAsync(pipe,request,cts.Token);
            var response = await PipeJson.ReadAsync<TResponse>(pipe,cts.Token);
            await process.WaitForExitAsync(cts.Token);DiagnosticLog.Write(worker,"worker.completed",$"Worker 退出码 {process.ExitCode}",requestId:pipeName);return response ?? throw new InvalidDataException($"{worker} 返回空响应");
        }
        catch(Exception ex) when(ex is EndOfStreamException or IOException&&!cts.IsCancellationRequested)
        {
            // Worker 崩溃时管道会先断开；“管道在消息完成前关闭”对用户没有意义，换成能照着做的说明。
            var exit=process.HasExited?$"（退出码 {process.ExitCode}）":"";
            await StopProcessAsync(process,worker,pipeName);
            throw new InvalidOperationException($"{worker} 意外退出{exit}，没有返回结果。请重试；反复出现时请确认是从完整的程序目录启动的。",ex);
        }
        catch
        {
            await StopProcessAsync(process,worker,pipeName);
            throw;
        }
    }

    internal static async Task StopProcessAsync(Process process,string worker,string requestId)
    {
        try
        {
            if(process.HasExited)return;
            process.Kill(entireProcessTree:true);
            var wait=process.WaitForExitAsync();
            await Task.WhenAny(wait,Task.Delay(2_000));
            DiagnosticLog.Write(worker,"worker.terminated","取消或异常后已终止 Worker",requestId:requestId);
        }
        catch(Exception ex){DiagnosticLog.Write(worker,"worker.terminate.failed",ex.Message,"Warning",requestId);}
    }
}

internal sealed class LongShotWorkerClient
{
    public Task<LongShotPairResponse> AnalyzeAsync(LongShotPairRequest request,CancellationToken token=default)=>
        OcrWorkerClient.RunAsync<LongShotPairRequest,LongShotPairResponse>("Gtools.LongShotWorker",request,TimeSpan.FromSeconds(30),token,
            new[]{"--operation","analyze"});
    public Task<LongShotResponse> StitchAsync(LongShotRequest request, CancellationToken token = default) =>
        OcrWorkerClient.RunAsync<LongShotRequest,LongShotResponse>("Gtools.LongShotWorker", request, TimeSpan.FromMinutes(10), token);

    public Task<LongShotCropResponse> CropAsync(LongShotCropRequest request,CancellationToken token=default)=>
        OcrWorkerClient.RunAsync<LongShotCropRequest,LongShotCropResponse>("Gtools.LongShotWorker",request,TimeSpan.FromMinutes(5),token,
            new[]{"--operation","crop"});
}
