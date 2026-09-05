using Gtools.App;
using Gtools.Core;

namespace Gtools.Tests;
public sealed class CaptureStabilityTests
{
    [Fact] public void StationaryContentMustStayStableForThreeHundredMilliseconds()
    {
        var state=new FrameStabilityTracker();var sample=new StabilitySample(64,64,new byte[4096]);
        Assert.False(state.Observe(sample,0));Assert.False(state.Observe(sample,299));Assert.True(state.Observe(sample,300));
    }
    [Fact] public void LocalAnimationOrLazyLoadResetsStabilityEvenWithoutScrolling()
    {
        var still=new StabilitySample(64,64,new byte[4096]);var values=new byte[4096];
        for(var y=10;y<18;y++)for(var x=10;x<18;x++)values[y*64+x]=240;
        var changed=new StabilitySample(64,64,values);var state=new FrameStabilityTracker();
        state.Observe(still,0);Assert.False(state.Observe(changed,280));
        Assert.False(state.Observe(changed,500));Assert.True(state.Observe(changed,581));
        Assert.False(state.Observe(still,600));
    }
    [Fact] public void EqualRowAveragesDoNotHideRepainting()
    {
        var a=new byte[80*80];var b=new byte[80*80];
        for(var y=0;y<80;y++)for(var x=0;x<80;x++){a[y*80+x]=(byte)(x<40?0:255);b[y*80+x]=(byte)(x<40?255:0);}
        Assert.False(new StabilitySample(80,80,a).IsSimilarTo(new(80,80,b)));
    }
    [Theory][InlineData(0)][InlineData(-30)][InlineData(500)]
    public void DuplicateReverseAndInsufficientOverlapAreRejected(int offset)=>
        Assert.Throws<InvalidOperationException>(()=>LongShotSeamGeometry.Validate(offset,550,600));
    [Fact] public void AnchorsRemainPhysicalPixelsAndSupportOnePixelAdjustments()
    {
        Assert.Equal(201,LongShotSeamGeometry.FromAnchors(340.8,140.1));
        LongShotSeamGeometry.Validate(201,350,600);
    }
    [Fact] public void WindowMovementResolutionAndDpiChangesPauseCapture()
    {
        var bounds=new System.Drawing.Rectangle(-1280,20,1000,800);
        Assert.True(LongShotTargetGuard.IsGeometryUnchanged(bounds,bounds,144,144));
        Assert.False(LongShotTargetGuard.IsGeometryUnchanged(bounds,bounds with{X=-1279},144,144));
        Assert.False(LongShotTargetGuard.IsGeometryUnchanged(bounds,bounds,144,192));
    }
    [Fact] public async Task TemporaryFramesAreRemovedAfterCancellationCleanup()
    {
        var dir=Path.Combine(Path.GetTempPath(),"Gtools.Tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir,"frame.png"),"synthetic");
        await LongShotTempCleanup.DeleteAsync(dir);Assert.False(Directory.Exists(dir));
    }
}
