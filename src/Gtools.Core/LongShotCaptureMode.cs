namespace Gtools.Core;

internal enum LongShotCaptureAction { Capture, Undo, Finish }

/// <summary>Only an active waiter accepts clicks. Review/processing never queues stale clicks.</summary>
internal sealed class LongShotCaptureModeState
{
    private TaskCompletionSource<LongShotCaptureAction>? _waiter;
    public bool Request(LongShotCaptureAction action)=>Interlocked.Exchange(ref _waiter,null)?.TrySetResult(action)==true;
    public async Task<LongShotCaptureAction> WaitAsync(CancellationToken token)
    {
        var waiter=new TaskCompletionSource<LongShotCaptureAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(Interlocked.CompareExchange(ref _waiter,waiter,null) is not null)throw new InvalidOperationException("已有采集等待。");
        using var registration=token.Register(()=>waiter.TrySetCanceled(token));
        try{return await waiter.Task;}
        finally{Interlocked.CompareExchange(ref _waiter,null,waiter);}
    }
}

internal sealed class LongShotSessionGate
{
    private int _active;
    public bool TryEnter()=>Interlocked.CompareExchange(ref _active,1,0)==0;
    public void Exit()=>Volatile.Write(ref _active,0);
    public bool IsActive=>Volatile.Read(ref _active)!=0;
}

/// <summary>Capture order is retained for undo; export normalizes both directions to top-to-bottom.</summary>
internal sealed class ReviewedLongShotSession(bool scrollUp)
{
    private readonly List<string> _frames=new();
    private readonly List<ConfirmedLongShotSeam> _seams=new();
    public IReadOnlyList<string> Frames=>_frames;
    public string? LastFrame=>_frames.LastOrDefault();
    public void AddFirst(string path){if(_frames.Count!=0)throw new InvalidOperationException();_frames.Add(path);}
    public void Add(string path,ConfirmedLongShotSeam seam,int height)
    {
        if(LastFrame is null)throw new InvalidOperationException();
        if(seam.UpperFrame!=(scrollUp?path:LastFrame)||seam.LowerFrame!=(scrollUp?LastFrame:path))
            throw new InvalidOperationException("接缝不属于当前相邻帧。");
        LongShotSeamGeometry.Validate(seam.Offset,seam.CutY,height);
        if(_seams.Count>0)
        {
            var old=_seams[^1];
            if(scrollUp?seam.CutY-seam.Offset>old.CutY:old.CutY-old.Offset>seam.CutY)
                throw new InvalidOperationException("接缝越过了上一处已确认边界，请调整接缝线。");
        }
        _frames.Add(path);_seams.Add(seam);
    }
    public string? Undo()
    {
        if(_frames.Count<=1)return null;
        var path=_frames[^1];_frames.RemoveAt(_frames.Count-1);_seams.RemoveAt(_seams.Count-1);return path;
    }
    public (string[] Frames,ConfirmedLongShotSeam[] Seams) Export()=>scrollUp
        ?(_frames.AsEnumerable().Reverse().ToArray(),_seams.AsEnumerable().Reverse().ToArray())
        :(_frames.ToArray(),_seams.ToArray());
    public long OutputHeight(int height)=>height+_seams.Sum(s=>(long)s.Offset);
}

public static class LongShotSeamGeometry
{
    public static int FromAnchors(double upperY,double lowerY)=>checked((int)Math.Round(upperY-lowerY));
    public static void Validate(int offset,int cutY,int height)
    {
        if(offset<8||offset>height*.70)throw new InvalidOperationException("画面重复、方向错误或重叠不足，请回退并保留至少半屏重叠。");
        if(cutY<offset||cutY>height)throw new InvalidOperationException("接缝线必须位于两帧重叠区域。");
    }
}
