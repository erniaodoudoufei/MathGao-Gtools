using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Gtools.Core;
using IDataObject = System.Windows.IDataObject;

namespace Gtools.App;

internal sealed class ClipboardHistoryService : IDisposable
{
    private readonly LocalDatabase _db;
    private readonly Func<int> _limit;
    private readonly HwndSource _source;
    private readonly CancellationTokenSource _lifetime=new();
    private readonly object _writeQueueLock=new();
    private readonly object _restoreLock=new();
    private Task _writeTail=Task.CompletedTask;
    private string? _restoredHash;
    private DateTimeOffset _restoreSuppressionUntil;
    public bool Paused { get; set; }
    public event EventHandler? Updated;

    public ClipboardHistoryService(LocalDatabase db, Func<int> limit)
    {
        _db = db; _limit = limit;
        _source = new HwndSource(new HwndSourceParameters("Gtools.ClipboardWindow") { Width=0, Height=0, WindowStyle=unchecked((int)0x80000000) });
        _source.AddHook(WndProc); NativeMethods.AddClipboardFormatListener(_source.Handle);
    }
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            if (!Paused) _source.Dispatcher.BeginInvoke(CaptureCurrent);
        }
        return IntPtr.Zero;
    }
    private void CaptureCurrent()
    {
        try
        {
            var data = Clipboard.GetDataObject(); if (data is null) return;
            string? text = GetString(data, DataFormats.UnicodeText); string? html = GetString(data, DataFormats.Html); string? rtf = GetString(data, DataFormats.Rtf);
            BitmapSource? bitmapCopy=null;var width=0;var height=0;
            if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
            {
                // Clipboard objects are tied to the UI dispatcher. Copy the pixels once and freeze the
                // result; PNG compression and SQLite I/O can then run off the UI thread in order.
                var copy=new WriteableBitmap(bitmap);copy.Freeze();bitmapCopy=copy;width=copy.PixelWidth;height=copy.PixelHeight;
            }
            if (string.IsNullOrEmpty(text) && bitmapCopy is null && string.IsNullOrEmpty(html) && string.IsNullOrEmpty(rtf)) return;
            Enqueue(new ClipboardSnapshot(text,html,rtf,bitmapCopy,width,height,DateTimeOffset.Now));
        }
        catch(Exception ex){DiagnosticLog.Write("clipboard","capture.snapshot.failed",ex.Message,"Warning");}
    }
    private void Enqueue(ClipboardSnapshot snapshot)
    {
        lock(_writeQueueLock)
        {
            _writeTail=_writeTail.ContinueWith(_=>Persist(snapshot),CancellationToken.None,
                TaskContinuationOptions.None,TaskScheduler.Default);
        }
    }
    private void Persist(ClipboardSnapshot snapshot)
    {
        if(_lifetime.IsCancellationRequested)return;
        try
        {
            byte[]? png=null;
            if(snapshot.Bitmap is not null)
                png=EncodePng(snapshot.Bitmap);
            if(_lifetime.IsCancellationRequested)return;
            var kind=png is not null?"image":(snapshot.Html is not null||snapshot.Rtf is not null?"rich-text":"text");
            var preview=png is not null?$"图片 {snapshot.Width}×{snapshot.Height}":NormalizePreview(snapshot.Text??StripMarkup(snapshot.Html??snapshot.Rtf??""));
            var hash=ComputeHash(snapshot.Text,snapshot.Html,snapshot.Rtf,png);
            if(ShouldIgnoreRestored(hash))return;
            if(_db.AddClipboard(new(0,kind,preview,snapshot.Text,snapshot.Html,snapshot.Rtf,png,false,snapshot.CreatedAt,hash),_limit()))
                Updated?.Invoke(this,EventArgs.Empty);
        }
        catch(Exception ex){DiagnosticLog.Write("clipboard","capture.persist.failed",ex.Message,"Warning");}
    }
    public void Restore(ClipboardEntry item)
    {
        var data = new DataObject();
        if (!string.IsNullOrEmpty(item.Text)) data.SetData(DataFormats.UnicodeText,item.Text);
        if (!string.IsNullOrEmpty(item.Html)) data.SetData(DataFormats.Html,item.Html);
        if (!string.IsNullOrEmpty(item.Rtf)) data.SetData(DataFormats.Rtf,item.Rtf);
        if (item.ImagePng is not null)
        {
            using var ms = new MemoryStream(item.ImagePng); var image = new BitmapImage(); image.BeginInit(); image.CacheOption=BitmapCacheOption.OnLoad; image.StreamSource=ms; image.EndInit(); image.Freeze(); data.SetData(DataFormats.Bitmap,image);
        }
        lock(_restoreLock){_restoredHash=item.Hash;_restoreSuppressionUntil=DateTimeOffset.UtcNow.AddSeconds(3);}
        Clipboard.SetDataObject(data,true);
    }
    private static string? GetString(IDataObject d,string format) => d.GetDataPresent(format) ? d.GetData(format) as string : null;
    private bool ShouldIgnoreRestored(string hash)
    {
        lock(_restoreLock)
        {
            if(_restoredHash is null||DateTimeOffset.UtcNow>_restoreSuppressionUntil){_restoredHash=null;return false;}
            if(!string.Equals(_restoredHash,hash,StringComparison.Ordinal))return false;
            _restoredHash=null;return true;
        }
    }
    private static string NormalizePreview(string s)
    {
        var normalized=string.Join(' ',s.Replace('\r',' ').Replace('\n',' ').Split(' ',StringSplitOptions.RemoveEmptyEntries)).Trim();
        return normalized[..Math.Min(160,normalized.Length)];
    }
    private static string StripMarkup(string s) => System.Text.RegularExpressions.Regex.Replace(s,"<[^>]+>"," ");
    internal static byte[] EncodePng(BitmapSource bitmap)
    {
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms=new MemoryStream();encoder.Save(ms);return ms.ToArray();
    }
    private static string ComputeHash(params object?[] values)
    {
        using var sha=SHA256.Create();
        foreach(var v in values)
        {
            var b=v switch { string s=>Encoding.UTF8.GetBytes(s), byte[] a=>a, _=>Array.Empty<byte>() };
            sha.TransformBlock(b,0,b.Length,null,0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(),0,0); return Convert.ToHexString(sha.Hash!);
    }
    public void Dispose()
    {
        _lifetime.Cancel();Updated=null;NativeMethods.RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(WndProc);_source.Dispose();
    }

    private sealed record ClipboardSnapshot(string? Text,string? Html,string? Rtf,BitmapSource? Bitmap,
        int Width,int Height,DateTimeOffset CreatedAt);
}
