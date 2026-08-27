using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Gtools.Core;
using OpenCvSharp;
using SkiaSharp;

namespace Gtools.LongShotWorker;

public static class WorkerProgram
{
    private const int MinimumValidOffset=8;

    private static async Task<int> Main(string[] args)
    {
        AppPaths.Initialize(Arg(args,"--data-root"));
        DiagnosticLog.Initialize(args.Contains("--diagnostics",StringComparer.OrdinalIgnoreCase));
        var pipe=Arg(args,"--pipe");
        if(pipe is null)return 2;

        await using var server=new NamedPipeServerStream(pipe,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync();
        return string.Equals(Arg(args,"--operation"),"crop",StringComparison.OrdinalIgnoreCase)
            ?await RunCropAsync(server)
            :await RunStitchAsync(server);
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

    public static LongShotResponse Stitch(LongShotRequest req)
    {
        if(req.FramePaths.Length==0)throw new InvalidOperationException("没有可拼接的帧。");
        using var first=DecodeBitmap(req.FramePaths[0])??throw new InvalidDataException("无法读取第一帧。");
        var width=first.Width;
        var height=first.Height;
        var threshold=double.IsFinite(req.MinimumConfidence)?Math.Clamp(req.MinimumConfidence,0,1):.62;
        var offsets=new List<int>();
        var confidences=new List<double>();
        var warnings=new List<string>();
        var seams=new List<SeamDiagnostic>();

        for(var i=0;i<req.FramePaths.Length-1;i++)
        {
            using var upper=Cv2.ImDecode(File.ReadAllBytes(req.FramePaths[i]),ImreadModes.Grayscale);
            using var lower=Cv2.ImDecode(File.ReadAllBytes(req.FramePaths[i+1]),ImreadModes.Grayscale);
            if(upper.Empty()||lower.Empty())throw new InvalidDataException($"无法读取第 {i+1} 个接缝的帧。");
            if(upper.Width!=width||lower.Width!=width||upper.Height!=height||lower.Height!=height)
                throw new InvalidDataException("长截图各帧的尺寸必须完全一致。");

            LongShotOffsetHint? trustedHint=null;
            if(req.OffsetHints is not null&&i<req.OffsetHints.Length)
            {
                var supplied=req.OffsetHints[i];
                if(IsTrustedHint(supplied,height))trustedHint=supplied;
                else warnings.Add($"第 {i+1} 个接缝的采集位移不可靠，Worker 已忽略该提示并使用完整搜索范围。");
            }

            var match=FindOffsetMatch(upper,lower,req.Profile,req.EdgeExclusionRatio,trustedHint);
            var confidence=match.Confidence;
            if(!match.HasValidOffset&&trustedHint is null)
            {
                var error=$"第 {i+1} 个接缝没有可信的实测位移，也找不到几何上有效的图像重叠；已停止生成，避免输出错位长图。";
                warnings.Add(error);
                DiagnosticLog.Write("longshot-worker","stitch.no-valid-overlap",error,"Warning",req.RequestId,
                    new Dictionary<string,object?>{{"seam",i+1},{"confidence",confidence}});
                return new(req.RequestId,false,null,Array.Empty<string>(),confidences.ToArray(),warnings.ToArray(),error,true);
            }

            var offset=match.Offset;
            var usedFallback=false;
            if(confidence<threshold||!match.HasValidOffset)
            {
                if(trustedHint is { } hint)
                {
                    offset=hint.Offset;
                    usedFallback=true;
                    warnings.Add($"⚠ 第 {i+1} 个接缝图像匹配置信度只有 {confidence:0.00}，已改用可信实测位移 {hint.Offset}px（实测置信度 {hint.Confidence:0.00}），请重点检查该处。");
                }
                else
                {
                    // 低置信度不等同于没有候选。保留几何上有效的最佳图像匹配，
                    // 但绝不再用固定高度比例伪造一个看似正常的接缝。
                    warnings.Add($"⚠ 第 {i+1} 个接缝图像匹配置信度只有 {confidence:0.00}；已采用几何上有效的最佳偏移 {offset}px，请重点检查该处。");
                }
            }

            offsets.Add(offset);
            confidences.Add(confidence);
            seams.Add(new(i+1,offset,confidence,usedFallback,match.RegionOffsets));
        }

        var totalLong=(long)height+offsets.Sum(x=>(long)x);
        if(totalLong>int.MaxValue||(long)width*totalLong>req.MaxOutputPixels)
            throw new InvalidOperationException($"长图达到安全上限（{width}×{totalLong}），请缩短本次采集后继续下一张。");
        var total=(int)totalLong;
        EnsureParentDirectory(req.OutputPath);
        using var surface=SKSurface.Create(new SKImageInfo(width,total,SKColorType.Bgra8888,SKAlphaType.Premul))
            ??throw new InvalidOperationException("无法为长图分配图像缓冲区。");
        surface.Canvas.Clear(SKColors.White);
        var y=0;
        Draw(surface.Canvas,req.FramePaths[0],0);
        for(var i=1;i<req.FramePaths.Length;i++)
        {
            y+=offsets[i-1];
            Draw(surface.Canvas,req.FramePaths[i],y);
        }
        using var image=surface.Snapshot();
        using(var data=image.Encode(SKEncodedImageFormat.Png,95))
        using(var fs=File.Create(req.OutputPath))data.SaveTo(fs);
        var segments=ExportSegments(image,req.OutputPath,req.SegmentHeight);
        var diagnostics=new LongShotDiagnostics(req.Profile,req.FramePaths.Length,width,total,(long)width*total,
            Process.GetCurrentProcess().PeakWorkingSet64,seams.ToArray(),warnings.ToArray(),DateTimeOffset.Now);
        if(!string.IsNullOrWhiteSpace(req.DiagnosticsPath))
        {
            EnsureParentDirectory(req.DiagnosticsPath);
            File.WriteAllText(req.DiagnosticsPath,JsonSerializer.Serialize(diagnostics,GtoolsJson.Options));
        }
        DiagnosticLog.Write("longshot-worker","stitch.completed",$"完成 {req.FramePaths.Length} 帧拼接",requestId:req.RequestId,
            data:new Dictionary<string,object?>{{"profile",req.Profile.ToString()},{"width",width},{"height",total},{"requiresReview",warnings.Count>0}});
        return new(req.RequestId,true,req.OutputPath,segments,confidences.ToArray(),warnings.ToArray(),null,warnings.Count>0,diagnostics);
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

    public static (int Offset,double Confidence) FindOffset(Mat upper,Mat lower)
    {
        var result=FindOffsetMatch(upper,lower,LongShotCaptureProfile.WeChat4,.06,null);
        return(result.Offset,result.Confidence);
    }

    public static (int Offset,double Confidence,int[] RegionOffsets) FindOffsetDetailed(Mat upper,Mat lower,
        LongShotCaptureProfile profile,double edgeExclusionRatio,LongShotOffsetHint? offsetHint=null)
    {
        LongShotOffsetHint? trusted=offsetHint is { } hint&&IsTrustedHint(hint,upper.Height)?hint:null;
        var result=FindOffsetMatch(upper,lower,profile,edgeExclusionRatio,trusted);
        return(result.Offset,result.Confidence,result.RegionOffsets);
    }

    private static OffsetMatch FindOffsetMatch(Mat upper,Mat lower,LongShotCaptureProfile profile,double edgeExclusionRatio,
        LongShotOffsetHint? offsetHint)
    {
        const int hintTolerance=56;
        if(upper.Empty()||lower.Empty()||upper.Width!=lower.Width||upper.Height<20||lower.Height<20)
            return new(0,0,Array.Empty<int>(),false);
        var margin=Math.Max(8,(int)(upper.Width*Math.Clamp(edgeExclusionRatio,.02,.18)));
        var x=margin;
        var w=upper.Width-margin*2;
        if(w<8)return new(0,0,Array.Empty<int>(),false);
        var templateH=Math.Min(160,Math.Max(16,upper.Height/10));
        templateH=Math.Min(templateH,upper.Height-2);
        var fractions=profile switch
        {
            LongShotCaptureProfile.QQ9=>new[]{.58,.72,.84},
            LongShotCaptureProfile.Generic=>new[]{.55,.70,.85},
            _=>new[]{.64,.79}
        };
        var candidates=fractions.Select(Candidate).ToArray();
        var valid=candidates.Where(c=>c.IsValid).ToArray();
        if(valid.Length==0)return new(0,0,candidates.Select(c=>c.Offset).ToArray(),false);

        // 多个采样区域一致时优先采用该簇；没有一致簇时保留置信度最高的
        // 几何有效候选，供调用方以 RequiresReview 的方式使用。
        var cluster=valid.Select(anchor=>valid.Where(c=>Math.Abs(c.Offset-anchor.Offset)<=28).ToArray())
            .OrderByDescending(group=>group.Length)
            .ThenByDescending(group=>group.Average(c=>c.Confidence))
            .First();
        var chosen=(cluster.Length>=2?cluster:valid).OrderByDescending(c=>c.Confidence).First();
        var confidence=cluster.Length>=2?cluster.Average(c=>c.Confidence):chosen.Confidence*.72;
        return new(chosen.Offset,confidence,candidates.Select(c=>c.Offset).ToArray(),true);

        OffsetCandidate Candidate(double fraction)
        {
            var templateY=Math.Min(upper.Height-templateH-2,(int)(upper.Height*fraction));
            using var template=new Mat(upper,new Rect(x,templateY,w,templateH));
            Cv2.MeanStdDev(template,out _,out var deviation);
            var hasTexture=deviation.Val0>=1.5;
            int searchTop,searchBottom;
            if(offsetHint is { } hint)
            {
                var expected=templateY-hint.Offset;
                searchTop=Math.Clamp(expected-hintTolerance,0,Math.Max(0,lower.Height-templateH-1));
                searchBottom=Math.Clamp(expected+hintTolerance+templateH,searchTop+templateH+1,lower.Height);
            }
            else
            {
                searchTop=0;
                searchBottom=Math.Min(lower.Height,(int)(upper.Height*.82)+templateH);
            }
            if(searchBottom-searchTop<templateH+1)
            {
                searchTop=0;
                searchBottom=Math.Min(lower.Height,templateH+1);
            }
            using var search=new Mat(lower,new Rect(x,searchTop,w,searchBottom-searchTop));
            using var result=new Mat();
            Cv2.MatchTemplate(search,template,result,TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result,out _,out var max,out _,out var at);
            var offset=templateY-(searchTop+at.Y);
            var validConfidence=double.IsFinite(max)&&max>0;
            var validGeometry=offset>=MinimumValidOffset&&offset<Math.Min(upper.Height,lower.Height);
            return new(offset,validConfidence?max:0,hasTexture&&validConfidence&&validGeometry);
        }
    }

    private static bool IsTrustedHint(LongShotOffsetHint hint,int frameHeight) =>
        hint.IsReliable&&hint.Offset>=MinimumValidOffset&&hint.Offset<frameHeight&&
        double.IsFinite(hint.Confidence)&&hint.Confidence>0&&hint.Confidence<=1;

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
    }

    private readonly record struct OffsetCandidate(int Offset,double Confidence,bool IsValid);
    private readonly record struct OffsetMatch(int Offset,double Confidence,int[] RegionOffsets,bool HasValidOffset);
}
