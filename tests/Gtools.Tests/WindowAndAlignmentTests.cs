using Gtools.Core;
using Gtools.LongShotWorker;
using SkiaSharp;

namespace Gtools.Tests;

public sealed class FrameAlignmentTests
{
    private const int Width=200,Height=400;

    // 每一行按绝对行号确定性地生成噪声，因此“同一行内容”在任何帧里都一致，
    // 相当于一张很长的页面被窗口截取出不同的片段。
    private static byte[] Slice(int topRow,int height=Height,Func<int,int,byte>? overlay=null)
    {
        var gray=new byte[Width*height];
        for(var y=0;y<height;y++)
        {
            var random=new Random(topRow+y);
            for(var x=0;x<Width;x++)gray[y*Width+x]=(byte)random.Next(256);
        }
        if(overlay is not null)
            for(var y=0;y<height;y++)
                for(var x=0;x<Width;x++)
                {
                    var v=overlay(x,y);if(v!=0)gray[y*Width+x]=v;
                }
        return gray;
    }

    private static FrameSignature Signature(byte[] gray,int height=Height)=>FrameSignature.FromGray(gray,Width,height,Width);

    [Theory]
    [InlineData(37)]
    [InlineData(137)]
    [InlineData(260)]
    public void EstimateShift_RecoversAKnownScrollDistance(int shift)
    {
        // 页面向上滚动 shift 像素：原来在第 shift 行的内容现在跑到了第 0 行。
        var before=Signature(Slice(shift));
        var after=Signature(Slice(0));
        var estimate=FrameAlignment.EstimateShift(before,after);
        Assert.True(estimate.IsReliable,$"confidence={estimate.Confidence}");
        Assert.Equal(shift,estimate.Shift);
    }

    [Fact]
    public void EstimateShift_IgnoresALocalAnimationLikeASticker()
    {
        const int shift=120;
        var before=Signature(Slice(shift));
        // 屏幕固定位置有一块一直在变的动图，整图哈希会因此永远判为“有新内容”。
        var after=Signature(Slice(0,Height,(x,y)=>x>=20&&x<70&&y>=150&&y<200?(byte)(17+x):(byte)0));
        var estimate=FrameAlignment.EstimateShift(before,after);
        Assert.True(estimate.IsReliable,$"confidence={estimate.Confidence}");
        Assert.Equal(shift,estimate.Shift);
    }

    [Fact]
    public void EstimateShift_ReportsZeroWhenNothingScrolled()
    {
        var frame=Signature(Slice(500));
        Assert.Equal(0,FrameAlignment.EstimateShift(frame,frame).Shift);
    }

    [Fact]
    public void EstimateShift_IsNotReliableOnFlatContent()
    {
        var flat=new byte[Width*Height];Array.Fill(flat,(byte)240);
        var estimate=FrameAlignment.EstimateShift(Signature(flat),Signature(flat));
        Assert.False(estimate.IsReliable);
    }
}

public sealed class LongShotHintTests
{
    [Fact]
    public void Stitch_FallsBackToTheMeasuredOffsetInsteadOfAGuessedRatio()
    {
        var root=Path.Combine(Path.GetTempPath(),"Gtools.Tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            // 两帧互不相关，模板匹配必然低置信度；此时应采用采集端实测的偏移而不是 0.68×高度的盲猜。
            const int width=320,height=420,hint=180;
            var random=new Random(4321);var paths=new List<string>();
            for(var index=0;index<2;index++)
            {
                var pixels=new byte[width*height*4];random.NextBytes(pixels);
                var path=Path.Combine(root,$"noise-{index}.png");
                using var bitmap=new SKBitmap(new SKImageInfo(width,height,SKColorType.Bgra8888,SKAlphaType.Premul));
                System.Runtime.InteropServices.Marshal.Copy(pixels,0,bitmap.GetPixels(),pixels.Length);
                using var image=SKImage.FromBitmap(bitmap);using var data=image.Encode(SKEncodedImageFormat.Png,90);
                using var stream=File.Create(path);data.SaveTo(stream);paths.Add(path);
            }
            var result=WorkerProgram.Stitch(new LongShotRequest("hint",paths.ToArray(),Path.Combine(root,"hint.png"),8000,
                Profile:LongShotCaptureProfile.Generic,OffsetHints:new[]{new LongShotOffsetHint(hint,.91,true)}));

            Assert.True(result.Success,result.Error);
            Assert.True(result.RequiresReview);
            Assert.True(result.Diagnostics!.Seams[0].UsedFallback);
            Assert.Equal(hint,result.Diagnostics.Seams[0].Offset);
            Assert.Equal(height+hint,result.Diagnostics.Height);
            using var stitched=SKBitmap.Decode(result.OutputPath);
            Assert.Equal(height+hint,stitched.Height);
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public void Stitch_UsesTheHintToNarrowTheSearchOnRepetitiveContent()
    {
        var root=Path.Combine(Path.GetTempPath(),"Gtools.Tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            const int width=360,total=1000,frameHeight=480,step=240;
            using var full=new SKBitmap(width,total);
            using(var canvas=new SKCanvas(full))
            {
                canvas.Clear(SKColors.White);
                var paint=new SKPaint{Color=SKColors.Black,TextSize=22};
                for(var y=26;y<total;y+=34)canvas.DrawText($"line {y:0000} 长截图接缝测试",10,y,paint);
                // 打破 34px 的行距周期，否则相邻文本行本身就是彼此的合理匹配。
                for(var y=0;y<total;y+=47)canvas.DrawRect((y*13)%260,y,60,6,new SKPaint{Color=new SKColor((byte)(y%251),70,160)});
            }
            var paths=new List<string>();var hints=new List<LongShotOffsetHint>();
            for(var y=0;y+frameHeight<=total;y+=step)
            {
                var path=Path.Combine(root,$"f-{y}.png");
                using var surface=SKSurface.Create(new SKImageInfo(width,frameHeight));
                surface.Canvas.DrawBitmap(full,new SKRect(0,y,width,y+frameHeight),new SKRect(0,0,width,frameHeight));
                using var image=surface.Snapshot();using var data=image.Encode(SKEncodedImageFormat.Png,95);
                using var stream=File.Create(path);data.SaveTo(stream);
                paths.Add(path);if(paths.Count>1)hints.Add(new(step,.94,true));
            }
            var result=WorkerProgram.Stitch(new LongShotRequest("repeat",paths.ToArray(),Path.Combine(root,"repeat.png"),8000,
                Profile:LongShotCaptureProfile.Generic,OffsetHints:hints.ToArray()));

            Assert.True(result.Success,result.Error);
            Assert.All(result.Diagnostics!.Seams,seam=>Assert.Equal(step,seam.Offset));
            Assert.All(result.Diagnostics.Seams,seam=>Assert.False(seam.UsedFallback));
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public void Stitch_DoesNotUseAnUnreliableHintToNarrowOrOverrideMatching()
    {
        var root=Path.Combine(Path.GetTempPath(),"Gtools.Tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            const int width=320,total=760,frameHeight=420,step=160;
            var pixels=new byte[width*total*4];new Random(90210).NextBytes(pixels);
            using var full=new SKBitmap(new SKImageInfo(width,total,SKColorType.Bgra8888,SKAlphaType.Premul));
            System.Runtime.InteropServices.Marshal.Copy(pixels,0,full.GetPixels(),pixels.Length);
            var paths=new List<string>();
            foreach(var top in new[]{0,step})
            {
                var path=Path.Combine(root,$"frame-{top}.png");
                using var surface=SKSurface.Create(new SKImageInfo(width,frameHeight));
                surface.Canvas.DrawBitmap(full,new SKRect(0,top,width,top+frameHeight),new SKRect(0,0,width,frameHeight));
                using var image=surface.Snapshot();using var data=image.Encode(SKEncodedImageFormat.Png,95);
                using var stream=File.Create(path);data.SaveTo(stream);paths.Add(path);
            }

            var result=WorkerProgram.Stitch(new LongShotRequest("unreliable",paths.ToArray(),Path.Combine(root,"result.png"),8000,
                Profile:LongShotCaptureProfile.Generic,OffsetHints:new[]{new LongShotOffsetHint(310,.08,false)}));

            Assert.True(result.Success,result.Error);
            Assert.Equal(step,result.Diagnostics!.Seams[0].Offset);
            Assert.False(result.Diagnostics.Seams[0].UsedFallback);
            Assert.True(result.RequiresReview);
            Assert.Contains(result.Warnings,message=>message.Contains("已忽略"));
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public void Stitch_StopsWhenNeitherAHintNorAValidImageOffsetExists()
    {
        var root=Path.Combine(Path.GetTempPath(),"Gtools.Tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            const int width=240,height=360;
            var paths=new List<string>();
            for(var index=0;index<2;index++)
            {
                var path=Path.Combine(root,$"flat-{index}.png");
                using var bitmap=new SKBitmap(width,height);bitmap.Erase(index==0?SKColors.White:new SKColor(245,245,245));
                using var image=SKImage.FromBitmap(bitmap);using var data=image.Encode(SKEncodedImageFormat.Png,95);
                using var stream=File.Create(path);data.SaveTo(stream);paths.Add(path);
            }
            var output=Path.Combine(root,"must-not-exist.png");

            var result=WorkerProgram.Stitch(new LongShotRequest("no-overlap",paths.ToArray(),output,8000,
                Profile:LongShotCaptureProfile.Generic));

            Assert.False(result.Success);
            Assert.Null(result.OutputPath);
            Assert.False(File.Exists(output));
            Assert.Contains("停止生成",result.Error);
        }
        finally{Directory.Delete(root,true);}
    }
}
