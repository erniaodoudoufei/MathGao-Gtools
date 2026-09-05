using System.Diagnostics;
using System.IO.Pipes;
using Gtools.Core;
using SkiaSharp;

namespace Gtools.Tests;

public sealed class PublishedPackageTests
{
    [Fact]
    public async Task PublishedLongShotWorker_StitchesPngWithoutFfmpeg()
    {
        var packageRoot=Environment.GetEnvironmentVariable("GTOOLS_PACKAGE_ROOT");
        if(string.IsNullOrWhiteSpace(packageRoot))return;

        Assert.True(Directory.Exists(packageRoot),$"Package root does not exist: {packageRoot}");
        Assert.Empty(Directory.EnumerateFiles(packageRoot,"opencv_videoio_ffmpeg*.dll",SearchOption.AllDirectories));
        var worker=Path.Combine(packageRoot,"Workers","Gtools.LongShotWorker","Gtools.LongShotWorker.exe");
        Assert.True(File.Exists(worker),$"Published worker does not exist: {worker}");

        var testRoot=Path.Combine(Path.GetTempPath(),"Gtools.PublishedPackageTests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        Process? process=null;
        try
        {
            const int width=360,fullHeight=640,frameHeight=400,step=240;
            using var full=new SKBitmap(width,fullHeight);
            using(var fullCanvas=new SKCanvas(full))
            {
                fullCanvas.Clear(SKColors.White);using var textPaint=new SKPaint{Color=SKColors.Black,TextSize=22,IsAntialias=true};
                for(var y=28;y<fullHeight;y+=31)
                {
                    fullCanvas.DrawText($"row-{y:000} package smoke",12,y,textPaint);
                    using var barPaint=new SKPaint{Color=new SKColor((byte)(40+y%190),(byte)(70+y*3%160),(byte)(90+y*7%150))};
                    fullCanvas.DrawRect(20+(y*11)%180,y+3,70+(y%80),7,barPaint);
                }
            }

            var frames=new List<string>();
            foreach(var top in new[]{0,step})
            {
                var frame=Path.Combine(testRoot,$"frame-{top}.png");
                using var surface=SKSurface.Create(new SKImageInfo(width,frameHeight));
                surface.Canvas.DrawBitmap(full,new SKRect(0,top,width,top+frameHeight),new SKRect(0,0,width,frameHeight));
                using var image=surface.Snapshot();using var data=image.Encode(SKEncodedImageFormat.Png,95);
                using var stream=File.Create(frame);data.SaveTo(stream);frames.Add(frame);
            }

            var output=Path.Combine(testRoot,"stitched.png");
            var pipeName="Gtools.Published."+Guid.NewGuid().ToString("N");
            var startInfo=new ProcessStartInfo(worker){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(worker)!};
            startInfo.ArgumentList.Add("--pipe");startInfo.ArgumentList.Add(pipeName);
            process=Process.Start(startInfo);Assert.NotNull(process);

            await using var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.InOut,PipeOptions.Asynchronous);
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await pipe.ConnectAsync(timeout.Token);
            var seam=new ConfirmedLongShotSeam(frames[0],frames[1],step,320,LongShotAlignmentSource.ManualAnchors,0);
            await PipeJson.WriteAsync(pipe,new LongShotRequest("package-smoke",frames.ToArray(),output,1200,ConfirmedSeams:new[]{seam}),timeout.Token);
            var response=await PipeJson.ReadAsync<LongShotResponse>(pipe,timeout.Token);
            Assert.NotNull(response);Assert.True(response!.Success,response.Error);Assert.True(File.Exists(output));
            await process!.WaitForExitAsync(timeout.Token);Assert.Equal(0,process.ExitCode);
        }
        finally
        {
            if(process is { HasExited:false })process.Kill(entireProcessTree:true);
            process?.Dispose();
            if(Directory.Exists(testRoot))Directory.Delete(testRoot,true);
        }
    }
}
