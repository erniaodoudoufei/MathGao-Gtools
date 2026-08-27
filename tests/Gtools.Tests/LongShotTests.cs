using Gtools.Core;
using Gtools.LongShotWorker;
using SkiaSharp;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace Gtools.Tests;

public sealed class LongShotTests
{
    [Fact]
    public async Task NamedPipeWorker_ProcessesSingleFrame()
    {
        var root=Path.Combine(Path.GetTempPath(),"Gtools.Tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var frame=Path.Combine(root,"frame.png");using(var bmp=new SKBitmap(160,200)){bmp.Erase(SKColors.White);using var image=SKImage.FromBitmap(bmp);using var data=image.Encode(SKEncodedImageFormat.Png,90);using var fs=File.Create(frame);data.SaveTo(fs);}
            var pipeName="Gtools.Test."+Guid.NewGuid().ToString("N");var worker=FindWorker();using var process=Process.Start(new ProcessStartInfo(worker,$"--pipe {pipeName}"){UseShellExecute=false,CreateNoWindow=true})!;
            await using var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.InOut,PipeOptions.Asynchronous);await pipe.ConnectAsync(10_000);
            var req=new LongShotRequest("pipe",new[]{frame},Path.Combine(root,"out.png"),8000);await PipeJson.WriteAsync(pipe,req);var response=await PipeJson.ReadAsync<LongShotResponse>(pipe);
            Assert.NotNull(response);Assert.True(response!.Success,response.Error);Assert.True(File.Exists(response.OutputPath));
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public async Task NamedPipeWorker_CropsAnImageInTheWorkerProcess()
    {
        var root=Path.Combine(Path.GetTempPath(),"Gtools.Tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            const int sourceWidth=96,sourceHeight=140;
            var source=Path.Combine(root,"source.png");
            using(var bitmap=new SKBitmap(sourceWidth,sourceHeight))
            {
                using var canvas=new SKCanvas(bitmap);canvas.Clear(SKColors.White);
                using var red=new SKPaint{Color=SKColors.Red};using var blue=new SKPaint{Color=SKColors.Blue};
                canvas.DrawRect(0,0,sourceWidth,70,red);canvas.DrawRect(0,70,sourceWidth,70,blue);
                using var image=SKImage.FromBitmap(bitmap);using var data=image.Encode(SKEncodedImageFormat.Png,95);
                using var stream=File.Create(source);data.SaveTo(stream);
            }
            var output=Path.Combine(root,"cropped.png");
            var pipeName="Gtools.Test."+Guid.NewGuid().ToString("N");var worker=FindWorker();
            using var process=Process.Start(new ProcessStartInfo(worker,$"--pipe {pipeName} --operation crop"){UseShellExecute=false,CreateNoWindow=true})!;
            await using var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.InOut,PipeOptions.Asynchronous);await pipe.ConnectAsync(10_000);
            var request=new LongShotCropRequest("crop",source,output,10,60,70,50);
            await PipeJson.WriteAsync(pipe,request);var response=await PipeJson.ReadAsync<LongShotCropResponse>(pipe);

            Assert.NotNull(response);Assert.True(response!.Success,response.Error);Assert.Equal(70,response.Width);Assert.Equal(50,response.Height);
            using var cropped=SKBitmap.Decode(output);Assert.NotNull(cropped);Assert.Equal(SKColors.Red,cropped.GetPixel(5,5));Assert.Equal(SKColors.Blue,cropped.GetPixel(5,25));
        }
        finally{Directory.Delete(root,true);}
    }

    private static string FindWorker()
    {
        var d=new DirectoryInfo(AppContext.BaseDirectory);while(d is not null&&!File.Exists(Path.Combine(d.FullName,"Gtools.sln")))d=d.Parent;Assert.NotNull(d);
        return Directory.EnumerateFiles(Path.Combine(d!.FullName,"src","Gtools.LongShotWorker","bin"),"Gtools.LongShotWorker.exe",SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).First();
    }

    [Fact]
    public void Stitch_ReconstructsOverlappingFrames()
    {
        var root=Path.Combine(Path.GetTempPath(),"Gtools.Tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            const int width=360,total=1100,frameHeight=500,step=300;using var full=new SKBitmap(width,total);using(var c=new SKCanvas(full)){c.Clear(SKColors.White);var paint=new SKPaint{Color=SKColors.Black,TextSize=24};for(var y=30;y<total;y+=37)c.DrawText($"row-{y:0000} Gtools 微信聊天内容",12,y,paint);for(var y=0;y<total;y+=53)c.DrawRect((y*7)%240,y,90,8,new SKPaint{Color=new SKColor((byte)(y%255),80,180)});}
            var paths=new List<string>();for(var y=0;y+frameHeight<=total;y+=step){var p=Path.Combine(root,$"f-{y}.png");using var s=SKSurface.Create(new SKImageInfo(width,frameHeight));s.Canvas.DrawBitmap(full,new SKRect(0,y,width,y+frameHeight),new SKRect(0,0,width,frameHeight));using var image=s.Snapshot();using var data=image.Encode(SKEncodedImageFormat.Png,95);using var fs=File.Create(p);data.SaveTo(fs);paths.Add(p);}
            var output=Path.Combine(root,"out.png");var diagnostics=Path.Combine(root,"seams.json");var result=WorkerProgram.Stitch(new LongShotRequest("t",paths.ToArray(),output,8000,Profile:LongShotCaptureProfile.WeChat4,DiagnosticsPath:diagnostics));Assert.True(result.Success,result.Error);Assert.True(File.Exists(output));Assert.True(File.Exists(diagnostics));Assert.NotNull(result.Diagnostics);Assert.Equal(LongShotCaptureProfile.WeChat4,result.Diagnostics!.Profile);using var stitched=SKBitmap.Decode(output);Assert.InRange(stitched.Height,900,1200);Assert.All(result.SeamConfidences,x=>Assert.True(x>.6,$"confidence={x}"));
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public void QQ9Profile_UsesThreeRegionsAndFlagsLowConfidence()
    {
        var root=Path.Combine(Path.GetTempPath(),"Gtools.Tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            const int width=320,height=420;var random=new Random(1234);var paths=new List<string>();
            for(var index=0;index<2;index++)
            {
                var pixels=new byte[width*height*4];random.NextBytes(pixels);var p=Path.Combine(root,$"random-{index}.png");
                using var bitmap=new SKBitmap(new SKImageInfo(width,height,SKColorType.Bgra8888,SKAlphaType.Premul));System.Runtime.InteropServices.Marshal.Copy(pixels,0,bitmap.GetPixels(),pixels.Length);using var image=SKImage.FromBitmap(bitmap);using var data=image.Encode(SKEncodedImageFormat.Png,90);using var stream=File.Create(p);data.SaveTo(stream);paths.Add(p);
            }
            var result=WorkerProgram.Stitch(new LongShotRequest("qq",paths.ToArray(),Path.Combine(root,"qq.png"),8000,MinimumConfidence:.62,Profile:LongShotCaptureProfile.QQ9,DiagnosticsPath:Path.Combine(root,"qq-seams.json")));
            Assert.True(result.Success,result.Error);Assert.True(result.RequiresReview);Assert.NotEmpty(result.Warnings);Assert.Equal(3,result.Diagnostics!.Seams[0].RegionOffsets.Length);Assert.Equal(LongShotCaptureProfile.QQ9,result.Diagnostics.Profile);
            Assert.InRange(result.Diagnostics.Seams[0].Offset,8,height-1);Assert.False(result.Diagnostics.Seams[0].UsedFallback);
            Assert.Contains(result.Warnings,message=>message.Contains("几何上有效的最佳偏移"));
        }
        finally{Directory.Delete(root,true);}
    }
}
