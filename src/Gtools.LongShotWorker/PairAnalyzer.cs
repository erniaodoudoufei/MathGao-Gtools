using Gtools.Core;
using OpenCvSharp;
using SkiaSharp;

namespace Gtools.LongShotWorker;

/// <summary>Analysis proposes an alignment; only an explicit confirmed seam authorizes composition.</summary>
public static class PairAnalyzer
{
    public static LongShotPairResponse Analyze(LongShotPairRequest request)
    {
        using var upper=Cv2.ImRead(request.UpperFrame,ImreadModes.Grayscale);
        using var lower=Cv2.ImRead(request.LowerFrame,ImreadModes.Grayscale);
        if(upper.Empty()||lower.Empty()||upper.Size()!=lower.Size()||upper.Height<80||upper.Width<80)
            return Failure("两帧尺寸不一致或无法读取，请重新框选。 ");
        var h=upper.Height;var w=upper.Width;
        // Only horizontal downsampling: every returned vertical coordinate remains a physical pixel.
        using var a=new Mat();using var b=new Mat();
        var workingWidth=Math.Min(w,512);
        Cv2.Resize(upper,a,new Size(workingWidth,h),0,0,InterpolationFlags.Area);
        Cv2.Resize(lower,b,new Size(workingWidth,h),0,0,InterpolationFlags.Area);
        using(var unchanged=new Mat())
        {
            Cv2.Absdiff(a,b,unchanged);
            if(Cv2.Mean(unchanged).Val0<.1)return Failure("画面与上一帧相同，请先滚动聊天再采集。");
        }
        var candidates=FindCandidates(a,b);
        var clusters=candidates.Select(anchor=>candidates.Where(c=>Math.Abs(c.Offset-anchor.Offset)<=2).ToArray())
            .OrderByDescending(group=>group.Length).ThenByDescending(group=>group.Average(c=>c.Score)).ToArray();
        var best=clusters.FirstOrDefault()??Array.Empty<Candidate>();
        var suggested=best.Length>=3&&best.Select(c=>c.Row).Distinct().Count()>=2;
        var offset=request.ManualOffset??(best.Length>0?(int)Math.Round(best.Average(c=>c.Offset)):0);
        if(request.ManualOffset is null&&!suggested)
            return Failure("没有唯一可靠的重叠位置。请在两帧中各点一次同一行文字以手工对齐，或回退后重采。");
        if(offset<8||offset>h*.70)return Failure("画面重复、方向相反或重叠不足。请沿所选方向小幅滚动，保留至少半屏重叠。");
        var rival=clusters.FirstOrDefault(g=>Math.Abs(g[0].Offset-offset)>4);
        if(request.ManualOffset is null&&rival is not null&&rival.Length>=best.Length*.75)
            return Failure("重复内容存在多个合理位置，请选择同一行文字手工对齐。");

        using var overlapA=new Mat(a,new Rect(0,offset,a.Width,h-offset));
        using var overlapB=new Mat(b,new Rect(0,0,b.Width,h-offset));
        using var diff=new Mat();Cv2.Absdiff(overlapA,overlapB,diff);
        using var changed=new Mat();Cv2.Threshold(diff,changed,16,255,ThresholdTypes.Binary);
        var changedRatio=Cv2.CountNonZero(changed)/(double)(changed.Rows*changed.Cols);
        var mean=Cv2.Mean(diff).Val0;
        var support=CountMatchingDetail(a,b,offset);
        var compatible=mean<=5&&changedRatio<=.045&&support>=3;
        var confidence=best.Length==0?0:best.Average(c=>c.Score);
        var cut=Math.Clamp(request.CutY??FindCut(diff,offset,h),offset,h);
        var localCut=cut-offset;
        var seamFrom=Math.Max(0,localCut-10);var seamTo=Math.Min(diff.Height,localCut+10);
        using var strip=new Mat(diff,new Rect(0,seamFrom,diff.Width,Math.Max(1,seamTo-seamFrom)));
        using var stripChanged=new Mat();Cv2.Threshold(strip,stripChanged,16,255,ThresholdTypes.Binary);
        var seamClean=Cv2.CountNonZero(stripChanged)/(double)(strip.Width*strip.Height)<=.015;
        var canConfirm=compatible&&seamClean;
        var message=!compatible?"重叠内容不一致，可能有遮挡、图片加载、错误对齐或新消息提示。请重采或重新选取同一行。"
            :!seamClean?"接缝线上有变化内容，请移动接缝线避开变化区域，或重采。"
            :changedRatio>.002?"重叠区存在局部变化。请检查图片、表情及固定提示，确认接缝和内容完整后加入。"
            :"请检查接缝：同一行应连续、没有重字或缺字。确认后才会加入长图。";
        WritePreviews(request,offset,cut,w,h);
        return new(request.RequestId,true,canConfirm,offset,cut,confidence,message,request.PreviewPath,
            OverlayPath(request.PreviewPath),w,h,changedRatio);

        LongShotPairResponse Failure(string message)=>new(request.RequestId,true,false,0,0,0,message,Width:upper.Width,Height:upper.Height);
    }

    private static List<Candidate> FindCandidates(Mat a,Mat b)
    {
        var found=new List<Candidate>();var tileW=Math.Max(24,a.Width/5);var tileH=Math.Clamp(a.Height/10,32,80);
        for(var row=1;row<=7;row++)
        {
            var y=Math.Min(a.Height-tileH,row*a.Height/9);
            for(var col=0;col<5;col++)
            {
                var x=Math.Min(a.Width-tileW,col*tileW);
                using var template=new Mat(a,new Rect(x,y,tileW,tileH));
                if(!HasVerticalDetail(template))continue;
                using var search=new Mat(b,new Rect(x,0,tileW,b.Height));using var scores=new Mat();
                Cv2.MatchTemplate(search,template,scores,TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(scores,out _,out var score,out _,out var at);
                var offset=y-at.Y;
                if(!double.IsFinite(score)||score<.90||offset<8||offset>a.Height*.70)continue;
                using(var neighborhood=new Mat(scores,new Rect(0,Math.Max(0,at.Y-4),1,Math.Min(scores.Height,at.Y+5)-Math.Max(0,at.Y-4))))neighborhood.SetTo(-1);
                Cv2.MinMaxLoc(scores,out double _,out double runner);
                if(score-runner<.025)continue;
                found.Add(new(offset,score,row));
            }
        }
        return found;
    }

    private static bool HasVerticalDetail(Mat image)
    {
        // Bubble vertical borders have contrast but no vertical detail; exclude them explicitly.
        using var top=new Mat(image,new Rect(0,0,image.Width,image.Height-1));
        using var bottom=new Mat(image,new Rect(0,1,image.Width,image.Height-1));using var dy=new Mat();
        Cv2.Absdiff(top,bottom,dy);using var mask=new Mat();Cv2.Threshold(dy,mask,12,255,ThresholdTypes.Binary);
        return Cv2.CountNonZero(mask)>=image.Width*image.Height*.025;
    }

    private static int CountMatchingDetail(Mat a,Mat b,int offset)
    {
        var count=0;var tw=Math.Max(16,a.Width/5);var th=Math.Min(48,(a.Height-offset)/3);
        if(th<8)return 0;
        for(var y=offset;y+th<=a.Height;y+=th)for(var x=0;x+tw<=a.Width;x+=tw)
        {
            using var ta=new Mat(a,new Rect(x,y,tw,th));if(!HasVerticalDetail(ta))continue;
            using var tb=new Mat(b,new Rect(x,y-offset,tw,th));using var d=new Mat();Cv2.Absdiff(ta,tb,d);
            if(Cv2.Mean(d).Val0<=3)count++;
        }
        return count;
    }

    private static int FindCut(Mat difference,int offset,int height)
    {
        var middle=(height-offset)/2;var best=middle;var cost=double.MaxValue;
        for(var y=12;y<difference.Height-12;y+=2)
        {
            using var row=new Mat(difference,new Rect(0,y-10,difference.Width,20));
            var c=Cv2.Mean(row).Val0+Math.Abs(y-middle)*.0005;
            if(c<cost){cost=c;best=y;}
        }
        return offset+best;
    }

    private static string OverlayPath(string path)=>Path.Combine(Path.GetDirectoryName(path)!,Path.GetFileNameWithoutExtension(path)+"-overlay.png");
    private static void WritePreviews(LongShotPairRequest req,int offset,int cut,int width,int height)
    {
        using var upper=SKBitmap.Decode(req.UpperFrame);using var lower=SKBitmap.Decode(req.LowerFrame);
        var from=Math.Max(offset,cut-150);var to=Math.Min(height,cut+150);var ph=Math.Max(1,to-from);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(req.PreviewPath))!);
        using var surface=SKSurface.Create(new SKImageInfo(width,ph));
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.Save();surface.Canvas.ClipRect(new SKRect(0,0,width,cut-from));surface.Canvas.DrawBitmap(upper,0,-from);surface.Canvas.Restore();
        surface.Canvas.Save();surface.Canvas.ClipRect(new SKRect(0,cut-from,width,ph));surface.Canvas.DrawBitmap(lower,0,offset-from);surface.Canvas.Restore();
        Save(surface,req.PreviewPath);
        surface.Canvas.Clear(SKColors.White);surface.Canvas.DrawBitmap(upper,0,-from);
        using var half=new SKPaint{Color=new SKColor(255,255,255,128)};surface.Canvas.DrawBitmap(lower,0,offset-from,half);
        Save(surface,OverlayPath(req.PreviewPath));
    }
    private static void Save(SKSurface surface,string path)
    {
        using var image=surface.Snapshot();using var data=image.Encode(SKEncodedImageFormat.Png,100);
        using var file=File.Create(path);data.SaveTo(file);
    }
    private readonly record struct Candidate(int Offset,double Score,int Row);
}
