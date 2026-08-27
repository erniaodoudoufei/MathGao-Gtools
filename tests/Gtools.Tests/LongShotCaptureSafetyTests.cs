using Gtools.Core;

namespace Gtools.Tests;

public sealed class LongShotCaptureSafetyTests
{
    [Fact]
    public void PhysicalSelectionSupportsNegativeMixedMonitorCoordinates()
    {
        var result=LongShotCaptureGeometry.FromPhysicalPoints(new(-1920,80),new(-120,980));
        Assert.Equal(new System.Drawing.Rectangle(-1920,80,1800,900),result);
    }

    [Fact]
    public void PhysicalSelectionIsDirectionIndependent()
    {
        var a=LongShotCaptureGeometry.FromPhysicalPoints(new(2400,1400),new(1800,900));
        var b=LongShotCaptureGeometry.FromPhysicalPoints(new(1800,900),new(2400,1400));
        Assert.Equal(a,b);Assert.Equal(600,a.Width);Assert.Equal(500,a.Height);
    }

    [Fact]
    public void SameMillisecondStillProducesUniqueOutputNames()
    {
        var now=new DateTime(2026,8,27,12,34,56,789);
        var first=LongShotOutputNaming.CreateFileName(now,"aaaaaaaa11111111");
        var second=LongShotOutputNaming.CreateFileName(now,"bbbbbbbb22222222");
        Assert.NotEqual(first,second);Assert.EndsWith("-aaaaaaaa.png",first);Assert.EndsWith("-bbbbbbbb.png",second);
    }
}
