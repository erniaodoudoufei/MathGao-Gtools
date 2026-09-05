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
    public async Task NamedPipeWorkerAnalyzesAdjacentFramesBeforeReview()
    {
        using var chat=new SyntheticChat(2);
        var pipeName="Gtools.Test."+Guid.NewGuid().ToString("N");
        using var process=Process.Start(new ProcessStartInfo(FindWorker(),$"--pipe {pipeName} --operation analyze"){UseShellExecute=false,CreateNoWindow=true})!;
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.InOut,PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        await PipeJson.WriteAsync(pipe,new LongShotPairRequest("analysis",chat.Frames[0],chat.Frames[1],Path.Combine(chat.Root,"pair.png")),timeout.Token);
        var response=await PipeJson.ReadAsync<LongShotPairResponse>(pipe,timeout.Token);
        Assert.NotNull(response);Assert.True(response!.Success,response.Error);Assert.Equal(SyntheticChat.Step,response.Offset);
        await process.WaitForExitAsync(timeout.Token);
    }

    [Fact]
    public async Task CancellationStopsAWarmWorkerBeforeCleaningSessionFrames()
    {
        using var chat=new SyntheticChat(2);var pipeName="Gtools.Test."+Guid.NewGuid().ToString("N");
        using var process=Process.Start(new ProcessStartInfo(FindWorker(),$"--pipe {pipeName} --operation analyze"){UseShellExecute=false,CreateNoWindow=true})!;
        await using var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.InOut,PipeOptions.Asynchronous);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));await pipe.ConnectAsync(timeout.Token);
        var watch=Stopwatch.StartNew();
        await Gtools.App.OcrWorkerClient.StopProcessAsync(process,"Gtools.LongShotWorker","synthetic-cancel");
        Assert.True(process.HasExited);Assert.True(watch.Elapsed<TimeSpan.FromSeconds(2));
        await Gtools.App.LongShotTempCleanup.DeleteAsync(chat.Root);Assert.False(Directory.Exists(chat.Root));
    }
}
