using Gtools.App;
using Gtools.Core;
using SkiaSharp;

namespace Gtools.Tests;

public sealed class ClipboardHistoryTests
{
    [Fact]
    public async Task ClipboardRowsDecodeAndEncodeFrozenImagesAwayFromTheUiThread()
    {
        var summary=new ClipboardEntrySummary(1,"image","图片 2048×1024",false,DateTimeOffset.Now,true);
        var row=new ClipboardViewRow(summary);Assert.Null(row.Thumbnail);

        using var surface=SKSurface.Create(new SKImageInfo(2048,1024));Assert.NotNull(surface);
        surface!.Canvas.Clear(SKColors.CornflowerBlue);using var source=surface.Snapshot();using var data=source.Encode(SKEncodedImageFormat.Png,90);
        var thumbnail=ClipboardViewRow.DecodeThumbnail(data.ToArray());

        Assert.NotNull(thumbnail);Assert.Equal(96,thumbnail!.PixelWidth);Assert.Equal(48,thumbnail.PixelHeight);Assert.True(thumbnail.IsFrozen);
        var encoded=await Task.Run(()=>ClipboardHistoryService.EncodePng(thumbnail));
        Assert.True(encoded.Length>8);Assert.Equal(new byte[]{137,80,78,71,13,10,26,10},encoded[..8]);
    }
}
