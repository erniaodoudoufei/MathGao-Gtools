using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Gtools.Core;
using OpenCvSharp;
using SkiaSharp;

namespace Gtools.LongShotWorker;

public static class WorkerProgram
{

    private static async Task<int> Main(string[] args)
    {
        AppPaths.Initialize(Arg(args,"--data-root"));
        DiagnosticLog.Initialize(args.Contains("--diagnostics",StringComparer.OrdinalIgnoreCase));
        var pipe=Arg(args,"--pipe");
        if(pipe is null)return 2;

        await using var server=new NamedPipeServerStream(pipe,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync();
        return Arg(args,"--operation") switch
        {
            "crop"=>await RunCropAsync(server),
            "analyze"=>await RunAnalyzeAsync(server),
            _=>await RunStitchAsync(server)
        };
    }

    private static async Task<int> RunStitchAsync(Stream stream)
    {
        var req=await PipeJson.ReadAsync<LongShotRequest>(stream);
        if(req is null)return 3;
        LongShotResponse result;
        try
        {
            result=Stitch(req);
        }
        catch(Exception ex)
        {
            DiagnosticLog.Write("longshot-worker","stitch.failed",ex.ToString(),"Error",req.RequestId);
            result=new(req.RequestId,false,null,Array.Empty<string>(),Array.Empty<double>(),Array.Empty<string>(),ex.Message);
        }
        await PipeJson.WriteAsync(stream,result);
        return result.Success?0:1;
    }

    private static async Task<int> RunCropAsync(Stream stream)
    {
        var req=await PipeJson.ReadAsync<LongShotCropRequest>(stream);
        if(req is null)return 3;
        LongShotCropResponse result;
        try
        {
            result=Crop(req);
        }
        catch(Exception ex)
        {
            DiagnosticLog.Write("longshot-worker","crop.failed",ex.ToString(),"Error",req.RequestId);
            result=new(req.RequestId,false,null,0,0,ex.Message);
        }
        await PipeJson.WriteAsync(stream,result);
        return result.Success?0:1;
    }

    private static async Task<int> RunAnalyzeAsync(Stream stream)
    {
        var request=await PipeJson.ReadAsync<LongShotPairRequest>(stream);
        if(request is null)return 3;
        LongShotPairResponse response;
        try{response=PairAnalyzer.Analyze(request);}
        catch(Exception ex){response=new(request.RequestId,false,false,0,0,0,"帧对分析失败",Error:ex.Message);}
        await PipeJson.WriteAsync(stream,response);
        return response.Success?0:1;
    }

    public static LongShotResponse Stitch(LongShotRequest req)
    {
        if(req.FramePaths.Length==0)throw new InvalidOperationException("没有可拼接的帧。");
        using var first=DecodeBitmap(req.FramePaths[0])??throw new InvalidDataException("无法读取第一帧。");
        var width=first.Width;var height=first.Height;
        var confirmed=req.ConfirmedSeams??Array.Empty<ConfirmedLongShotSeam>();
        if(confirmed.Length!=req.FramePaths.Length-1)
            return Rejected("每个接缝都必须经过预览确认；未确认的位移提示不能用于导出。");
        var offsets=new List<long>{0};
        var boundaries=new List<long>{0};
        var diagnostics=new List<SeamDiagnostic>();
        for(var i=0;i<confirmed.Length;i++)
        {
            var seam=confirmed[i];
            if(seam.UpperFrame!=req.FramePaths[i]||seam.LowerFrame!=req.FramePaths[i+1])
                return Rejected("已确认接缝与帧顺序不一致。");
            LongShotSeamGeometry.Validate(seam.Offset,seam.CutY,height);
            using var next=DecodeBitmap(req.FramePaths[i+1])??throw new InvalidDataException("无法读取相邻帧。");
            if(next.Width!=width||next.Height!=height)return Rejected("采集期间画面尺寸改变，请重新框选。");
            var boundary=offsets[i]+seam.CutY;
            if(boundary<boundaries[^1])return Rejected("已确认接缝相互交叉，请撤销后重新确认。");
            boundaries.Add(boundary);offsets.Add(offsets[i]+seam.Offset);
            diagnostics.Add(new(i+1,seam.Offset,seam.Confidence,false,Array.Empty<int>(),seam.CutY,seam.Source.ToString()));
        }
        var totalLong=offsets[^1]+height;
        if(totalLong>int.MaxValue||width*totalLong>req.MaxOutputPixels)
            throw new InvalidOperationException($"长图达到安全上限（{width}×{totalLong}），请撤销最后一帧后导出。");
        var total=(int)totalLong;boundaries.Add(totalLong);
        EnsureParentDirectory(req.OutputPath);
        using var surface=SKSurface.Create(new SKImageInfo(width,total,SKColorType.Bgra8888,SKAlphaType.Premul))
            ??throw new InvalidOperationException("无法分配长图缓冲区。");
        surface.Canvas.Clear(SKColors.White);
        for(var i=0;i<req.FramePaths.Length;i++)
        {
            // Each output row belongs to exactly one source frame. No overlapping full-frame redraw.
            var from=boundaries[i];var to=boundaries[i+1];
            if(from<offsets[i]||to>offsets[i]+height)return Rejected("接缝超出帧内容，已停止导出。");
            if(to==from)continue;
            using var bitmap=DecodeBitmap(req.FramePaths[i])??throw new InvalidDataException("无法读取采集帧。");
            surface.Canvas.Save();surface.Canvas.ClipRect(new SKRect(0,from,width,to));
            surface.Canvas.DrawBitmap(bitmap,0,offsets[i]);surface.Canvas.Restore();
        }
        using var image=surface.Snapshot();
        using(var data=image.Encode(SKEncodedImageFormat.Png,100))
        using(var file=File.Create(req.OutputPath))data.SaveTo(file);
        var segments=ExportSegments(image,req.OutputPath,req.SegmentHeight);
        var report=new LongShotDiagnostics(req.Profile,req.FramePaths.Length,width,total,width*totalLong,
            Process.GetCurrentProcess().PeakWorkingSet64,diagnostics.ToArray(),Array.Empty<string>(),DateTimeOffset.Now);
        if(!string.IsNullOrWhiteSpace(req.DiagnosticsPath))
        {
            EnsureParentDirectory(req.DiagnosticsPath);
            File.WriteAllText(req.DiagnosticsPath,JsonSerializer.Serialize(report,GtoolsJson.Options));
        }
        return new(req.RequestId,true,req.OutputPath,segments,confirmed.Select(s=>s.Confidence).ToArray(),
            Array.Empty<string>(),Diagnostics:report);

        LongShotResponse Rejected(string message)=>new(req.RequestId,false,null,Array.Empty<string>(),
            Array.Empty<double>(),Array.Empty<string>(),message,true);
    }

    public static LongShotCropResponse Crop(LongShotCropRequest req)
    {
        if(string.IsNullOrWhiteSpace(req.SourcePath)||!File.Exists(req.SourcePath))
            throw new FileNotFoundException("找不到待裁剪的长图。",req.SourcePath);
        if(req.X<0||req.Y<0||req.Width<=0||req.Height<=0)
            throw new ArgumentOutOfRangeException(nameof(req),"裁剪矩形必须位于图像内且宽高大于零。");
        if((long)req.Width*req.Height>req.MaxOutputPixels)
            throw new InvalidOperationException($"裁剪结果达到安全上限（{req.Width}×{req.Height}）。");

        using var source=DecodeBitmap(req.SourcePath)??throw new InvalidDataException("无法解码待裁剪的长图。");
        if((long)req.X+req.Width>source.Width||(long)req.Y+req.Height>source.Height)
            throw new ArgumentOutOfRangeException(nameof(req),"裁剪矩形超出原图范围。");

        EnsureParentDirectory(req.OutputPath);
        using var surface=SKSurface.Create(new SKImageInfo(req.Width,req.Height,SKColorType.Bgra8888,SKAlphaType.Premul))
            ??throw new InvalidOperationException("无法为裁剪结果分配图像缓冲区。");
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawBitmap(source,
            new SKRect(req.X,req.Y,req.X+req.Width,req.Y+req.Height),
            new SKRect(0,0,req.Width,req.Height));
        using var image=surface.Snapshot();
        using(var data=image.Encode(SKEncodedImageFormat.Png,95))
        using(var fs=File.Create(req.OutputPath))data.SaveTo(fs);
        DiagnosticLog.Write("longshot-worker","crop.completed",$"完成 {req.Width}×{req.Height} 长图裁剪",requestId:req.RequestId,
            data:new Dictionary<string,object?>{{"width",req.Width},{"height",req.Height}});
        return new(req.RequestId,true,req.OutputPath,req.Width,req.Height);
    }

    private static SKBitmap? DecodeBitmap(string path)=>SKBitmap.Decode(File.ReadAllBytes(path));

    private static void Draw(SKCanvas canvas,string path,int y)
    {
        using var bitmap=DecodeBitmap(path)??throw new InvalidDataException($"无法读取 {path}");
        canvas.DrawBitmap(bitmap,0,y);
    }

    private static string[] ExportSegments(SKImage full,string output,int height)
    {
        if(height<=0||full.Height<=height)return Array.Empty<string>();
        var list=new List<string>();
        const int overlap=80;
        var index=1;
        for(var y=0;y<full.Height;y+=Math.Max(1,height-overlap))
        {
            var h=Math.Min(height,full.Height-y);
            using var surface=SKSurface.Create(new SKImageInfo(full.Width,h))
                ??throw new InvalidOperationException("无法为长图分段分配图像缓冲区。");
            surface.Canvas.Clear(SKColors.White);
            surface.Canvas.DrawImage(full,new SKRect(0,y,full.Width,y+h),new SKRect(0,0,full.Width,h));
            using var image=surface.Snapshot();
            using var data=image.Encode(SKEncodedImageFormat.Png,95);
            var path=Path.Combine(Path.GetDirectoryName(output)!,Path.GetFileNameWithoutExtension(output)+$"-part-{index:00}.png");
            using var fs=File.Create(path);
            data.SaveTo(fs);
            list.Add(path);
            index++;
            if(y+h>=full.Height)break;
        }
        return list.ToArray();
    }

    private static void EnsureParentDirectory(string path)
    {
        var parent=Path.GetDirectoryName(Path.GetFullPath(path));
        if(!string.IsNullOrWhiteSpace(parent))Directory.CreateDirectory(parent);
    }

    private static string? Arg(string[] args,string key)
    {
        var i=Array.IndexOf(args,key);
        return i>=0&&i+1<args.Length?args[i+1]:null;
    }}
