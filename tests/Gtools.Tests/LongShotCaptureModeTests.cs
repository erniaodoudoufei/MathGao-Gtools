using Gtools.Core;

namespace Gtools.Tests;
public sealed class LongShotCaptureModeTests
{
    [Fact] public async Task ReviewAndProcessingCannotQueueCaptureClicks()
    {
        var state=new LongShotCaptureModeState();
        Assert.False(state.Request(LongShotCaptureAction.Capture));
        var wait=state.WaitAsync(CancellationToken.None);
        Assert.True(state.Request(LongShotCaptureAction.Capture));
        Assert.False(state.Request(LongShotCaptureAction.Capture));
        Assert.Equal(LongShotCaptureAction.Capture,await wait);
        Assert.False(state.Request(LongShotCaptureAction.Finish));
    }
    [Fact] public async Task CancelRemovesWaiterAndAllowsNextSession()
    {
        var state=new LongShotCaptureModeState();using var cancellation=new CancellationTokenSource();
        var waiting=state.WaitAsync(cancellation.Token);cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>waiting);
        Assert.False(state.Request(LongShotCaptureAction.Capture));
        var next=state.WaitAsync(CancellationToken.None);Assert.True(state.Request(LongShotCaptureAction.Finish));
        Assert.Equal(LongShotCaptureAction.Finish,await next);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void ConfirmUndoAndExportRetainCorrectOrder(bool up)
    {
        var s=new ReviewedLongShotSession(up);s.AddFirst("first");
        var seam=new ConfirmedLongShotSeam(up?"next":"first",up?"first":"next",200,350,LongShotAlignmentSource.ManualAnchors,0);
        s.Add("next",seam,600);
        Assert.Equal(up?new[]{"next","first"}:new[]{"first","next"},s.Export().Frames);
        Assert.Equal(800,s.OutputHeight(600));
        Assert.Equal("next",s.Undo());Assert.Single(s.Export().Frames);Assert.Empty(s.Export().Seams);
        Assert.Null(s.Undo());
    }
    [Fact] public void UnrelatedSeamCannotBeAccepted()
    {
        var s=new ReviewedLongShotSession(false);s.AddFirst("first");
        Assert.Throws<InvalidOperationException>(()=>s.Add("next",new("other","next",200,350,LongShotAlignmentSource.Suggested,1),600));
        Assert.Single(s.Frames);
    }
    [Fact] public void SessionGateAllowsOnlyOneCapture()
    {
        var gate=new LongShotSessionGate();Assert.True(gate.TryEnter());Assert.False(gate.TryEnter());
        gate.Exit();Assert.True(gate.TryEnter());
    }
}
