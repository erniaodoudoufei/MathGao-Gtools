using Gtools.App;
using SkiaSharp;

namespace Gtools.Tests;

public sealed class LongShotPreviewTests
{
    [Fact]
    public async Task TallImagePreviewUsesDecodeWidthAndReleasesTheSourceFile()
    {
        var root=Path.Combine(Path.GetTempPath(),"gtools-preview-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var path=Path.Combine(root,"tall.png");
        try
        {
            // 1,600 × 18,000 × 4 bytes is about 110 MiB when fully decoded.
            using(var surface=SKSurface.Create(new SKImageInfo(1600,18_000)))
            {
                Assert.NotNull(surface);surface!.Canvas.Clear(new SKColor(246,246,246));
                using var paint=new SKPaint{Color=SKColors.DarkSlateGray,StrokeWidth=3};
                for(var y=40;y<18_000;y+=160)surface.Canvas.DrawLine(40,y,1560,y,paint);
                using var image=surface.Snapshot();using var data=image.Encode(SKEncodedImageFormat.Png,90);using var stream=File.Create(path);data.SaveTo(stream);
            }

            Exception? error=null;System.Windows.Media.Imaging.BitmapSource? preview=null;
            var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread=new Thread(()=>
            {
                try{preview=LongShotPreviewWindow.LoadPreview(path);}
                catch(Exception ex){error=ex;}
                finally{finished.SetResult();}
            });
            thread.SetApartmentState(ApartmentState.STA);thread.Start();await finished.Task;
            if(error is not null)throw error;
            Assert.NotNull(preview);Assert.Equal(1200,preview!.PixelWidth);Assert.True(preview.PixelHeight<18_000);Assert.True(preview.IsFrozen);

            // OnLoad must release the file instead of keeping the original long PNG locked.
            using var exclusive=File.Open(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        }
        finally{try{Directory.Delete(root,true);}catch{}}
    }
}
