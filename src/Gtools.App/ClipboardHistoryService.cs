using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Gtools.Core;

namespace Gtools.App;

internal sealed class ClipboardHistoryService : IDisposable
{
    private readonly LocalDatabase _db;
    private readonly Func<int> _limit;
    private readonly IClipboardReader _reader;
    private readonly bool _listen;
    private readonly Func<ClipboardSnapshot,CancellationToken,Task<bool>> _persist;
    private readonly Channel<ClipboardSnapshot> _queue=Channel.CreateUnbounded<ClipboardSnapshot>(new(){SingleReader=true,SingleWriter=true,AllowSynchronousContinuations=false});
    private readonly CancellationTokenSource _abort=new();
    private readonly TaskCompletionSource _ready=new(TaskCreationOptions.RunContinuationsAsynchronously),_threadExited=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _stateLock=new(),_stopLock=new();
    private readonly Task _writer;
    private Dispatcher _dispatcher=null!;
    private HwndSource? _source;
    private DispatcherTimer? _retry;
    private int _accepting=1,_paused,_pending,_saved,_failures,_attempt;
    private bool _reading,_retryPending;private uint? _lastCaptured,_retrySequence;
    private long _lastSavedAt;private string? _lastError;private Task<bool>? _stop;
    private static readonly int[] RetryDelays=[30,75,150,300,600];
    public bool Paused {get=>Volatile.Read(ref _paused)!=0;set=>Volatile.Write(ref _paused,value?1:0);}
    public event EventHandler? Updated;
    public event EventHandler? StateChanged;
    public ClipboardSaveState State {get{lock(_stateLock)return new(_pending,_saved,_failures,_lastError,_reading||_retryPending,_lastSavedAt);}}

    public ClipboardHistoryService(LocalDatabase db,Func<int> limit):this(db,limit,null,null,true){}
    internal ClipboardHistoryService(LocalDatabase db,Func<int> limit,IClipboardReader? reader,
        Func<ClipboardSnapshot,CancellationToken,Task<bool>>? persist=null,bool listen=false)
    {
        _db=db;_limit=limit;_reader=reader??new NativeClipboardReader(Guid.NewGuid().ToString("N"));_listen=listen;_persist=persist??PersistAsync;
        _writer=Task.Run(WriteLoopAsync);
        var thread=new Thread(ReadLoop){IsBackground=true,Name="Gtools Clipboard STA"};thread.SetApartmentState(ApartmentState.STA);thread.Start();
        try{_ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();}
        catch{Interlocked.Exchange(ref _accepting,0);_queue.Writer.TryComplete();throw;}
    }
    private void ReadLoop()
    {
        try
        {
            _dispatcher=Dispatcher.CurrentDispatcher;
            _source=new HwndSource(new HwndSourceParameters("Gtools.ClipboardWindow"){Width=0,Height=0,WindowStyle=unchecked((int)0x80000000)});
            if(_reader is NativeClipboardReader native)native.OwnerWindow=_source.Handle;
            _source.AddHook(WndProc);
            if(_listen&&!NativeMethods.AddClipboardFormatListener(_source.Handle))throw new InvalidOperationException("无法监听系统剪贴板。");
            _ready.TrySetResult();if(Volatile.Read(ref _accepting)!=0)Dispatcher.Run();
        }
        catch(Exception ex){_ready.TrySetException(ex);Fail("剪贴板监听已停止。",ex);}
        finally
        {
            _retry?.Stop();lock(_stateLock)_retryPending=false;
            if(_source is not null){if(_listen)NativeMethods.RemoveClipboardFormatListener(_source.Handle);_source.RemoveHook(WndProc);_source.Dispose();}
            _queue.Writer.TryComplete();_threadExited.TrySetResult();
        }
    }
    private IntPtr WndProc(IntPtr hwnd,int msg,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if(msg==NativeMethods.WM_CLIPBOARDUPDATE)CaptureCurrent();return IntPtr.Zero;
    }
    internal Task NotifyChangedAsync()=>_dispatcher.InvokeAsync(CaptureCurrent).Task;
    private void CaptureCurrent()
    {
        _retry?.Stop();
        lock(_stateLock)_retryPending=false;
        if(Paused||Volatile.Read(ref _accepting)==0||_reading){Notify(StateChanged);return;}
        var sequence=_reader.SequenceNumber;
        if(sequence!=0&&_lastCaptured==sequence)return;
        if(_retrySequence!=sequence){_attempt=0;_retrySequence=sequence;}
        lock(_stateLock)_reading=true;Notify(StateChanged);
        var started=Stopwatch.GetTimestamp();
        try
        {
            var snapshot=_reader.Read();
            // Read returns the sequence while locked, including any delayed format rendering.
            if(snapshot is null){_lastCaptured=sequence;_attempt=0;return;}
            if(snapshot.Sequence!=0&&snapshot.Sequence==_lastCaptured)return;
            _lastCaptured=snapshot.Sequence;_attempt=0;
            if(snapshot.IsOwnRestore)return;
            lock(_stateLock)_pending++;
            if(!_queue.Writer.TryWrite(snapshot)){lock(_stateLock)_pending--;return;}
            Log("capture.snapshot",snapshot,new(){["snapshotMs"]=Stopwatch.GetElapsedTime(started).TotalMilliseconds});
        }
        catch(Exception ex)
        {
            if(_attempt<RetryDelays.Length)
            {
                var delay=RetryDelays[_attempt++];
                _retry??=new DispatcherTimer(DispatcherPriority.Normal,_dispatcher);
                _retry.Stop();_retry.Interval=TimeSpan.FromMilliseconds(delay);_retry.Tick-=Retry;_retry.Tick+=Retry;_retry.Start();
                lock(_stateLock)_retryPending=true;
                DiagnosticLog.Write("clipboard","capture.retry","剪贴板读取重试",data:new Dictionary<string,object?>{{"sequence",sequence},{"attempt",_attempt},{"errorType",ex.GetType().Name}});
            }
            else{_lastCaptured=sequence;Fail("最近一次复制未能读取，请重新复制。",ex);}
        }
        finally{lock(_stateLock)_reading=false;Notify(StateChanged);}
    }
    private void Retry(object? sender,EventArgs e)=>CaptureCurrent();
    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach(var snapshot in _queue.Reader.ReadAllAsync(_abort.Token))
            {
                try
                {
                    Log("capture.queue",snapshot,new(){["waitMs"]=Stopwatch.GetElapsedTime(snapshot.CapturedAt).TotalMilliseconds});
                    var added=await _persist(snapshot,_abort.Token).ConfigureAwait(false);
                    if(added){lock(_stateLock){_saved++;_lastSavedAt=Stopwatch.GetTimestamp();}Notify(Updated);}
                }
                catch(OperationCanceledException) when(_abort.IsCancellationRequested){}
                catch(Exception ex){Fail("有一条剪贴板记录保存失败，请重新复制。",ex);}
                finally{lock(_stateLock)_pending--;Notify(StateChanged);}
            }
        }
        catch(OperationCanceledException) when(_abort.IsCancellationRequested){}
        finally{while(_queue.Reader.TryRead(out _)){lock(_stateLock)_pending--;}Notify(StateChanged);}
    }
    private Task<bool> PersistAsync(ClipboardSnapshot snapshot,CancellationToken token)
    {
        var started=Stopwatch.GetTimestamp();var image=ClipboardImageCodec.Prepare(snapshot);token.ThrowIfCancellationRequested();
        var preparedAt=Stopwatch.GetTimestamp();var png=image.Png;
        var kind=png is not null?"image":(snapshot.Html is not null||snapshot.Rtf is not null?"rich-text":"text");
        var preview=png is not null?$"图片 {image.Width}×{image.Height}":NormalizePreview(snapshot.Text??StripMarkup(snapshot.Html??snapshot.Rtf??""));
        var hash=ComputeHash(snapshot.Text,snapshot.Html,snapshot.Rtf,png);var hashedAt=Stopwatch.GetTimestamp();
        token.ThrowIfCancellationRequested();
        var added=_db.AddClipboard(new(0,kind,preview,snapshot.Text,snapshot.Html,snapshot.Rtf,png,false,snapshot.CreatedAt,hash),_limit());
        Log("capture.persist",snapshot,new(){["width"]=image.Width,["height"]=image.Height,["prepareMs"]=Stopwatch.GetElapsedTime(started,preparedAt).TotalMilliseconds,
            ["hashMs"]=Stopwatch.GetElapsedTime(preparedAt,hashedAt).TotalMilliseconds,["databaseMs"]=Stopwatch.GetElapsedTime(hashedAt).TotalMilliseconds,["added"]=added});
        return Task.FromResult(added);
    }
    internal async Task RestoreAsync(ClipboardEntry entry)
    {
        if(_reader is not NativeClipboardReader native)throw new InvalidOperationException("测试读取器不写系统剪贴板。");
        var values=await Task.Run(()=>native.PrepareRestore(entry));
        for(var attempt=0;;attempt++)
        {
            try{await _dispatcher.InvokeAsync(()=>native.Restore(values));return;}
            catch(System.Runtime.InteropServices.ExternalException) when(attempt<RetryDelays.Length){await Task.Delay(RetryDelays[attempt]);}
        }
    }
    private void Fail(string message,Exception ex)
    {
        lock(_stateLock){_lastError=message;_failures++;}
        DiagnosticLog.Write("clipboard","capture.failed",message,"Warning",data:new Dictionary<string,object?>{{"errorType",ex.GetType().Name},{"hresult",ex.HResult}});
        Notify(StateChanged);
    }
    private void Notify(EventHandler? handler)
    {
        if(handler is null)return;
        foreach(EventHandler subscriber in handler.GetInvocationList())try{subscriber(this,EventArgs.Empty);}catch{/* Observers cannot break persistence. */}
    }
    private static void Log(string name,ClipboardSnapshot snapshot,Dictionary<string,object?> data)
    {
        data["sequence"]=snapshot.Sequence;data["format"]=snapshot.Png is not null?"PNG":snapshot.Dib is not null?"DIB":"text";
        DiagnosticLog.Write("clipboard",name,"剪贴板处理阶段",data:data);
    }
    private static string NormalizePreview(string s)
    {
        var normalized=string.Join(' ',s.Replace('\r',' ').Replace('\n',' ').Split(' ',StringSplitOptions.RemoveEmptyEntries)).Trim();return normalized[..Math.Min(160,normalized.Length)];
    }
    private static string StripMarkup(string s)=>System.Text.RegularExpressions.Regex.Replace(s,"<[^>]+>"," ");
    internal static byte[] EncodePng(BitmapSource bitmap)
    {
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=new MemoryStream();encoder.Save(stream);return stream.ToArray();
    }
    private static string ComputeHash(params object?[] values)
    {
        using var sha=SHA256.Create();foreach(var value in values){var bytes=value switch{string s=>Encoding.UTF8.GetBytes(s),byte[] data=>data,_=>Array.Empty<byte>()};sha.TransformBlock(bytes,0,bytes.Length,null,0);}
        sha.TransformFinalBlock(Array.Empty<byte>(),0,0);return Convert.ToHexString(sha.Hash!);
    }
    public Task<bool> StopAsync(TimeSpan? timeout=null){lock(_stopLock)return _stop??=StopCoreAsync(timeout??TimeSpan.FromSeconds(3));}
    private async Task<bool> StopCoreAsync(TimeSpan timeout)
    {
        // The STA finishes any snapshot already being copied before completing the write queue.
        Interlocked.Exchange(ref _accepting,0);
        if(!_dispatcher.HasShutdownStarted)_dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        var all=Task.WhenAll(_writer,_threadExited.Task);
        if(await Task.WhenAny(all,Task.Delay(timeout)).ConfigureAwait(false)==all){await all.ConfigureAwait(false);return State.FailureCount==0;}
        Fail("退出等待已超时，仍有已捕获内容未保存。",new TimeoutException());_abort.Cancel();_queue.Writer.TryComplete();return false;
    }
    public void Dispose(){StopAsync().GetAwaiter().GetResult();Updated=null;StateChanged=null;}
}
