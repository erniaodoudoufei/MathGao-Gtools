using Gtools.Core;

namespace Gtools.Tests;

public sealed class DesktopLayerTests
{
    [Fact]
    public void SelectHost_UsesTheWindowThatActuallyHostsShellDefView()
    {
        var candidates=new[]
        {
            new DesktopLayerGeometry.HostCandidate((nint)10,"Progman",false),
            new DesktopLayerGeometry.HostCandidate((nint)20,"WorkerW",true),
            new DesktopLayerGeometry.HostCandidate((nint)30,"WorkerW",false)
        };

        Assert.Equal((nint)20,DesktopLayerGeometry.SelectHost(candidates));
    }

    [Fact]
    public void SelectHost_RejectsUnrelatedWindowsEvenIfTheyContainAViewWithThatName()
    {
        var candidates=new[]
        {
            new DesktopLayerGeometry.HostCandidate((nint)10,"Chrome_WidgetWin_1",true),
            new DesktopLayerGeometry.HostCandidate((nint)20,"Progman",false)
        };

        Assert.Equal(0,DesktopLayerGeometry.SelectHost(candidates));
    }

    [Fact]
    public void OwnedPopupStyle_RemovesChildAndTopmostButKeepsPopup()
    {
        const long wsChild=0x40000000L,wsPopup=0x80000000L,wsExTopmost=0x8L;
        var normalized=DesktopLayerGeometry.NormalizeOwnedPopupStyles(wsChild,wsExTopmost|0x80);

        Assert.Equal(0,normalized.Style&wsChild);
        Assert.NotEqual(0,normalized.Style&wsPopup);
        Assert.Equal(0,normalized.ExStyle&wsExTopmost);
        Assert.NotEqual(0,normalized.ExStyle&0x80);
    }

    [Fact]
    public void ZOrder_DoesNothingWhenDockIsAlreadyImmediatelyAboveDesktop()
    {
        Assert.Null(DesktopLayerGeometry.ResolveDesktopInsertAfter((nint)20,(nint)20));
    }

    [Fact]
    public void ZOrder_InsertsAfterTheWindowCurrentlyAboveDesktop()
    {
        Assert.Equal((nint)30,DesktopLayerGeometry.ResolveDesktopInsertAfter((nint)30,(nint)20));
        Assert.Equal((nint)0,DesktopLayerGeometry.ResolveDesktopInsertAfter((nint)0,(nint)20));
    }

    [Fact]
    public void ScreenCoordinatesBecomeHostClientPhysicalPixels()
    {
        var point=DesktopLayerGeometry.ScreenDipToHostClientPixels(1200,600,1.5,1.5,100,-50);

        Assert.Equal(1700,point.X);
        Assert.Equal(950,point.Y);
    }

    [Fact]
    public void WindowPixelsRoundTripBackToPersistedScreenDip()
    {
        var point=DesktopLayerGeometry.ScreenPixelsToDip(1800,900,1.5,1.5);

        Assert.Equal(1200,point.Left);
        Assert.Equal(600,point.Top);
    }

    [Theory]
    [InlineData(150,75,1.5,100,50)]
    [InlineData(-200,100,2,-100,50)]
    public void DragDeltaRespectsCurrentDpi(int pixelX,int pixelY,double scale,double expectedX,double expectedY)
    {
        var delta=DesktopLayerGeometry.PixelDeltaToDip(pixelX,pixelY,scale,scale);

        Assert.Equal(expectedX,delta.X);
        Assert.Equal(expectedY,delta.Y);
    }

    [Fact]
    public void InvalidDpiScaleFallsBackToOne()
    {
        var delta=DesktopLayerGeometry.PixelDeltaToDip(12,34,double.NaN,0);

        Assert.Equal(12,delta.X);
        Assert.Equal(34,delta.Y);
    }
}
