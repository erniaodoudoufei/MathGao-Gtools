using System.IO.Pipes;
using System.Text.Json;
using System.Security.Cryptography;
using Gtools.Core;
using PaddleOCRSharp;

namespace Gtools.OcrWorker;

internal static class WorkerProgram
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        Environment.CurrentDirectory=AppContext.BaseDirectory;
        AppPaths.Initialize(GetArg(args,"--data-root"));DiagnosticLog.Initialize(args.Contains("--diagnostics",StringComparer.OrdinalIgnoreCase));
        var pipe=GetArg(args,"--pipe");if(string.IsNullOrWhiteSpace(pipe))return 2;
        await using var server=new NamedPipeServerStream(pipe,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync();var request=await PipeJson.ReadAsync<OcrRequest>(server);if(request is null)return 3;
        OcrResponse response;
        try
        {
            AppPaths.EnsureAll();VerifyBundledModels();var parameter=new OCRParameter{cpu_math_library_num_threads=Math.Max(2,Environment.ProcessorCount/2),enable_mkldnn=true,cls=false,det=true,use_angle_cls=false,det_db_score_mode=true};
            var engine=new PaddleOCREngine(null,parameter);File.WriteAllText(Path.Combine(AppPaths.Models,"paddleocr.ready"),DateTimeOffset.Now.ToString("O"));
            var result=engine.DetectText(Convert.FromBase64String(request.ImageBase64));var regions=new List<OcrRegion>();
            if(result?.TextBlocks is not null)foreach(var block in result.TextBlocks)regions.Add(ToRegion(block));
            response=new(request.RequestId,true,string.Join(Environment.NewLine,regions.Select(x=>x.Text)),regions.ToArray());
        }
        catch(Exception ex){DiagnosticLog.Write("ocr-worker","ocr.failed",ex.ToString(),"Error",request.RequestId);response=new(request.RequestId,false,"",Array.Empty<OcrRegion>(),ex.Message);}
        await PipeJson.WriteAsync(server,response);return response.Success?0:1;
    }
    private static OcrRegion ToRegion(object block)
    {
        var t=block.GetType();var text=t.GetProperty("Text")?.GetValue(block)?.ToString()??"";var score=Convert.ToSingle(t.GetProperty("Score")?.GetValue(block)??0f);var points=new List<OcrPoint>();
        if(t.GetProperty("BoxPoints")?.GetValue(block) is System.Collections.IEnumerable boxes)foreach(var p in boxes){if(p is null)continue;var pt=p.GetType();points.Add(new(Convert.ToSingle(pt.GetProperty("X")?.GetValue(p)??0),Convert.ToSingle(pt.GetProperty("Y")?.GetValue(p)??0)));}
        return new(text,score,points.ToArray());
    }
    private static void VerifyBundledModels()
    {
        var root=Path.Combine(AppContext.BaseDirectory,"inference");if(!Directory.Exists(root))throw new DirectoryNotFoundException("PaddleOCR 离线模型目录缺失。");var files=Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Where(p=>p.EndsWith(".json",StringComparison.OrdinalIgnoreCase)||p.EndsWith(".pdiparams",StringComparison.OrdinalIgnoreCase)||p.EndsWith(".txt",StringComparison.OrdinalIgnoreCase)).ToArray();if(files.Length<5)throw new InvalidDataException("PaddleOCR 模型文件不完整。");
        var marker=Path.Combine(AppPaths.Models,"paddleocr-model-manifest.json");if(File.Exists(marker))return;var manifest=files.Select(p=>new{path=Path.GetRelativePath(root,p),length=new FileInfo(p).Length,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))}).ToArray();File.WriteAllText(marker,JsonSerializer.Serialize(manifest,GtoolsJson.Options));
    }
    private static string? GetArg(string[] args,string key){var i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:null;}
}
