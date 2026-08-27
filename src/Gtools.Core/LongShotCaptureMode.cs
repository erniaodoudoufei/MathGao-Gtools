namespace Gtools.Core;

internal enum LongShotCaptureMode
{
    Manual,
    Automatic
}

internal enum LongShotCaptureAction
{
    ManualCapture,
    AutomaticStep,
    CaptureAndFinish,
    FinishWithoutCapture
}

/// <summary>
/// Coordinates the progress window with the capture loop. Manual requests are accepted only while
/// the loop is waiting, so rapid or stale button clicks cannot queue extra frames.
/// </summary>
internal sealed class LongShotCaptureModeState
{
    private readonly object _gate=new();
    private LongShotCaptureMode _mode=LongShotCaptureMode.Manual;
    private TaskCompletionSource<LongShotCaptureAction>? _waiter;
    private LongShotCaptureAction? _pendingPriorityAction;

    public LongShotCaptureMode Mode
    {
        get{lock(_gate)return _mode;}
    }

    public bool RequestManualCapture()
    {
        TaskCompletionSource<LongShotCaptureAction>? waiter;
        lock(_gate)
        {
            if(_mode!=LongShotCaptureMode.Manual||_waiter is null)return false;
            waiter=_waiter;_waiter=null;
        }
        return waiter.TrySetResult(LongShotCaptureAction.ManualCapture);
    }

    public bool RequestCaptureAndFinish()=>RequestPriorityAction(LongShotCaptureAction.CaptureAndFinish);
    public bool RequestFinishWithoutCapture()=>RequestPriorityAction(LongShotCaptureAction.FinishWithoutCapture);

    private bool RequestPriorityAction(LongShotCaptureAction action)
    {
        TaskCompletionSource<LongShotCaptureAction>? waiter;
        lock(_gate)
        {
            if(_pendingPriorityAction is not null)return false;
            waiter=_waiter;_waiter=null;
            if(waiter is null)_pendingPriorityAction=action;
        }
        if(waiter is null)return true;
        if(waiter.TrySetResult(action))return true;
        lock(_gate)
        {
            if(_pendingPriorityAction is null){_pendingPriorityAction=action;return true;}
        }
        return false;
    }

    public void SetMode(LongShotCaptureMode mode)
    {
        TaskCompletionSource<LongShotCaptureAction>? waiter=null;
        lock(_gate)
        {
            if(_mode==mode)return;
            _mode=mode;
            if(mode==LongShotCaptureMode.Automatic){waiter=_waiter;_waiter=null;}
        }
        waiter?.TrySetResult(LongShotCaptureAction.AutomaticStep);
    }

    public async Task<LongShotCaptureAction> WaitForNextActionAsync(CancellationToken token)
    {
        TaskCompletionSource<LongShotCaptureAction> waiter;
        lock(_gate)
        {
            if(_pendingPriorityAction is { } pending)
            {
                _pendingPriorityAction=null;
                return pending;
            }
            if(_mode==LongShotCaptureMode.Automatic)return LongShotCaptureAction.AutomaticStep;
            if(_waiter is not null)throw new InvalidOperationException("A long-shot capture wait is already active.");
            waiter=new(TaskCreationOptions.RunContinuationsAsynchronously);_waiter=waiter;
        }
        using var registration=token.Register(()=>waiter.TrySetCanceled(token));
        try{return await waiter.Task.ConfigureAwait(false);}
        finally
        {
            lock(_gate)if(ReferenceEquals(_waiter,waiter))_waiter=null;
        }
    }
}

/// <summary>Prevents two region selectors and capture loops from running at the same time.</summary>
internal sealed class LongShotSessionGate
{
    private int _active;
    public bool TryEnter()=>Interlocked.CompareExchange(ref _active,1,0)==0;
    public void Exit()=>Volatile.Write(ref _active,0);
    public bool IsActive=>Volatile.Read(ref _active)!=0;
}
