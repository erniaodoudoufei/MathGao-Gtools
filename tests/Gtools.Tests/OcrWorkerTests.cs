using System.Diagnostics;
using System.IO.Pipes;
using Gtools.Core;
using SkiaSharp;

namespace Gtools.Tests;

public sealed class OcrWorkerTests
{
    [Fact]
    public async Task PaddleWorker_RecognizesSimpleEnglishText()
    {
        using var bitmap=new SKBitmap(720,150);using(var canvas=new SKCanvas(bitmap)){canvas.Clear(SKColors.White);using var paint=new SKPaint{Color=SKColors.Black,TextSize=64,IsAntialias=true,Typeface=SKTypeface.FromFamilyName("Arial")};canvas.DrawText("Gtools OCR 123",25,100,paint);}
        byte[] png;using(var image=SKImage.FromBitmap(bitmap))using(var data=image.Encode(SKEncodedImageFormat.Png,95))png=data.ToArray();
        var pipeName="Gtools.OcrTest."+Guid.NewGuid().ToString("N");var worker=FindWorker();using var process=Process.Start(new ProcessStartInfo(worker,$"--pipe {pipeName}"){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(worker)!})!;
        await using var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.InOut,PipeOptions.Asynchronous);await pipe.ConnectAsync(10_000);await PipeJson.WriteAsync(pipe,new OcrRequest("ocr",Convert.ToBase64String(png)));
        using var cts=new CancellationTokenSource(TimeSpan.FromMinutes(2));var response=await PipeJson.ReadAsync<OcrResponse>(pipe,cts.Token);Assert.NotNull(response);Assert.True(response!.Success,response.Error);Assert.Contains("123",response.Text);
    }
    private static string FindWorker()
    {
        var d=new DirectoryInfo(AppContext.BaseDirectory);while(d is not null&&!File.Exists(Path.Combine(d.FullName,"Gtools.sln")))d=d.Parent;Assert.NotNull(d);
        return Directory.EnumerateFiles(Path.Combine(d!.FullName,"src","Gtools.OcrWorker","bin"),"Gtools.OcrWorker.exe",SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).First();
    }
}
