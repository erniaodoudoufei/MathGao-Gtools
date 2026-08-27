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
    private int _suppress;
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
            if (_suppress > 0) { _suppress--; return IntPtr.Zero; }
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
            byte[]? png = null;
            if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var ms = new MemoryStream(); encoder.Save(ms); png = ms.ToArray();
            }
            if (string.IsNullOrEmpty(text) && png is null && string.IsNullOrEmpty(html) && string.IsNullOrEmpty(rtf)) return;
            var kind = png is not null ? "image" : (html is not null || rtf is not null ? "rich-text" : "text");
            var preview = png is not null ? $"图片 {bitmapWidth(png)}" : NormalizePreview(text ?? StripMarkup(html ?? rtf ?? ""));
            var hash = ComputeHash(text, html, rtf, png);
            if (_db.AddClipboard(new(0,kind,preview,text,html,rtf,png,false,DateTimeOffset.Now,hash), _limit())) Updated?.Invoke(this,EventArgs.Empty);
        }
        catch { }
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
        _suppress += 2; Clipboard.SetDataObject(data,true);
    }
    private static string? GetString(IDataObject d,string format) => d.GetDataPresent(format) ? d.GetData(format) as string : null;
    private static string NormalizePreview(string s) => string.Join(' ',s.Replace('\r',' ').Replace('\n',' ').Split(' ',StringSplitOptions.RemoveEmptyEntries)).Trim()[..Math.Min(160,string.Join(' ',s.Replace('\r',' ').Replace('\n',' ').Split(' ',StringSplitOptions.RemoveEmptyEntries)).Trim().Length)];
    private static string StripMarkup(string s) => System.Text.RegularExpressions.Regex.Replace(s,"<[^>]+>"," ");
    private static string bitmapWidth(byte[] png)
    {
        try { using var ms=new MemoryStream(png); var d=BitmapDecoder.Create(ms,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None); return $"{d.Frames[0].PixelWidth}×{d.Frames[0].PixelHeight}"; } catch { return ""; }
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
    public void Dispose() { NativeMethods.RemoveClipboardFormatListener(_source.Handle); _source.RemoveHook(WndProc); _source.Dispose(); }
}
