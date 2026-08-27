using Gtools.Core;

namespace Gtools.Tests;

public sealed class LongShotCaptureModeTests
{
    [Fact]
    public void NewCaptureStartsInManualMode()
    {
        var state=new LongShotCaptureModeState();
        Assert.Equal(LongShotCaptureMode.Manual,state.Mode);
        Assert.False(state.RequestManualCapture());
    }

    [Fact]
    public async Task ManualButtonReleasesExactlyOneWaitingCapture()
    {
        var state=new LongShotCaptureModeState();
        var wait=state.WaitForNextActionAsync(CancellationToken.None);
        Assert.True(state.RequestManualCapture());
        Assert.False(state.RequestManualCapture());
        Assert.Equal(LongShotCaptureAction.ManualCapture,await wait);
        Assert.False(state.RequestManualCapture());
    }

    [Fact]
    public async Task SwitchingToAutomaticWakesManualWaiter()
    {
        var state=new LongShotCaptureModeState();
        var wait=state.WaitForNextActionAsync(CancellationToken.None);
        state.SetMode(LongShotCaptureMode.Automatic);
        Assert.Equal(LongShotCaptureAction.AutomaticStep,await wait);
        Assert.Equal(LongShotCaptureAction.AutomaticStep,await state.WaitForNextActionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SwitchingBackToManualRequiresANewButtonClick()
    {
        var state=new LongShotCaptureModeState();
        state.SetMode(LongShotCaptureMode.Automatic);
        Assert.Equal(LongShotCaptureAction.AutomaticStep,await state.WaitForNextActionAsync(CancellationToken.None));
        state.SetMode(LongShotCaptureMode.Manual);

        var wait=state.WaitForNextActionAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);Assert.True(state.RequestManualCapture());
        Assert.Equal(LongShotCaptureAction.ManualCapture,await wait);
    }

    [Fact]
    public async Task CancellationEndsAManualWaitWithoutLeavingAStaleRequest()
    {
        var state=new LongShotCaptureModeState();using var cancellation=new CancellationTokenSource();
        var wait=state.WaitForNextActionAsync(cancellation.Token);cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>wait);
        Assert.False(state.RequestManualCapture());
    }

    [Fact]
    public async Task CaptureAndFinishInterruptsAWaitingManualCapture()
    {
        var state=new LongShotCaptureModeState();
        var wait=state.WaitForNextActionAsync(CancellationToken.None);
        Assert.True(state.RequestCaptureAndFinish());
        Assert.Equal(LongShotCaptureAction.CaptureAndFinish,await wait);
    }

    [Fact]
    public async Task FinishRequestedDuringAutomaticStepIsDeliveredNext()
    {
        var state=new LongShotCaptureModeState();state.SetMode(LongShotCaptureMode.Automatic);
        Assert.Equal(LongShotCaptureAction.AutomaticStep,await state.WaitForNextActionAsync(CancellationToken.None));
        Assert.True(state.RequestFinishWithoutCapture());
        Assert.Equal(LongShotCaptureAction.FinishWithoutCapture,await state.WaitForNextActionAsync(CancellationToken.None));
    }

    [Fact]
    public void SessionGateAllowsOnlyOneCaptureUntilReleased()
    {
        var gate=new LongShotSessionGate();
        Assert.True(gate.TryEnter());Assert.True(gate.IsActive);
        Assert.False(gate.TryEnter());
        gate.Exit();Assert.False(gate.IsActive);Assert.True(gate.TryEnter());
    }
}
