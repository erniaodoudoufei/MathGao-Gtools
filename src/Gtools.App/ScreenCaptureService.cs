using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Gtools.Core;

namespace Gtools.App;

internal static class ScreenCaptureService
{
    public static System.Drawing.Rectangle? SelectRegion()
    {
        var overlay = new RegionCaptureWindow();
        return overlay.ShowDialog() == true && overlay.SelectedRegion.Width >= 2 && overlay.SelectedRegion.Height >= 2 ? overlay.SelectedRegion : null;
    }
    public static async Task<byte[]?> SelectAndCaptureAsync()
    {
        var region = SelectRegion();
        if (region is null) return null;
        await Task.Delay(100);
        return Capture(region.Value);
    }

    public static byte[] Capture(System.Drawing.Rectangle rect)
    {
        using var bitmap = Grab(rect);
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); return stream.ToArray();
    }

    /// <summary>一次抓屏同时产出 PNG 和逐行特征，长截图用特征实测滚动距离。</summary>
    public static CapturedFrame CaptureFrame(System.Drawing.Rectangle rect, double edgeExclusionRatio)
    {
        using var bitmap = Grab(rect);
        var signature = Describe(bitmap, edgeExclusionRatio);
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png);
        return new(stream.ToArray(), signature);
    }

    /// <summary>只取特征、不编码 PNG，用于滚动动画是否停稳的轮询。</summary>
    public static FrameSignature CaptureSignature(System.Drawing.Rectangle rect, double edgeExclusionRatio)
    {
        using var bitmap = Grab(rect); return Describe(bitmap, edgeExclusionRatio);
    }

    private static Bitmap Grab(System.Drawing.Rectangle rect)
    {
        var bitmap = new Bitmap(rect.Width, rect.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, rect.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    // 长截图等待滚动停稳时每 70ms 就要取一次特征，这里复用缓冲区，避免持续往大对象堆扔几 MB 的数组。
    [ThreadStatic] private static byte[]? _pixelScratch;
    [ThreadStatic] private static byte[]? _grayScratch;

    private static FrameSignature Describe(Bitmap bitmap, double edgeExclusionRatio)
    {
        var area = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var locked = bitmap.LockBits(area, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            var stride = Math.Abs(locked.Stride);
            var needed = stride * bitmap.Height;
            if (_pixelScratch is null || _pixelScratch.Length < needed) _pixelScratch = new byte[needed];
            var grayNeeded = bitmap.Width * bitmap.Height;
            if (_grayScratch is null || _grayScratch.Length < grayNeeded) _grayScratch = new byte[grayNeeded];
            System.Runtime.InteropServices.Marshal.Copy(locked.Scan0, _pixelScratch, 0, needed);
            var gray = FrameSignature.ToGray(_pixelScratch, bitmap.Width, bitmap.Height, stride, _grayScratch);
            return FrameSignature.FromGray(gray, bitmap.Width, bitmap.Height, bitmap.Width, edgeExclusionRatio);
        }
        finally { bitmap.UnlockBits(locked); }
    }
}

internal sealed record CapturedFrame(byte[] Png, FrameSignature Signature);

internal sealed class RegionCaptureWindow : Window
{
    private readonly System.Windows.Shapes.Rectangle _selection = new() { Stroke = System.Windows.Media.Brushes.White, StrokeThickness = 2, Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(30,255,90,20)) };
    private readonly System.Windows.Controls.Canvas _canvas = new();
    private System.Windows.Point _start;
    private System.Drawing.Point _startScreen;
    private System.Drawing.Point _endScreen;
    public System.Drawing.Rectangle SelectedRegion { get; private set; }
    public RegionCaptureWindow()
    {
        Title = "Gtools 区域选择"; WindowStyle = WindowStyle.None; AllowsTransparency = true;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(70,0,0,0)); Topmost = true; ShowInTaskbar = false;
        Left = SystemParameters.VirtualScreenLeft; Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth; Height = SystemParameters.VirtualScreenHeight;
        Content = _canvas; Cursor = System.Windows.Input.Cursors.Cross; _canvas.Children.Add(_selection); _selection.Visibility = Visibility.Collapsed;
        MouseLeftButtonDown += (_, e) =>
        {
            _start=e.GetPosition(_canvas);_startScreen=GetPhysicalCursor(_start);
            _selection.Visibility=Visibility.Visible;CaptureMouse();
        };
        MouseMove += (_, e) => { if (!IsMouseCaptured) return; SetSelection(_start, e.GetPosition(_canvas)); };
        MouseLeftButtonUp += (_, e) =>
        {
            if(!IsMouseCaptured)return;var end=e.GetPosition(_canvas);SetSelection(_start,end);
            _endScreen=GetPhysicalCursor(end);ReleaseMouseCapture();Finish();
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; Close(); } };
    }
    private void SetSelection(System.Windows.Point a, System.Windows.Point b)
    {
        var x=Math.Min(a.X,b.X); var y=Math.Min(a.Y,b.Y); var w=Math.Abs(a.X-b.X); var h=Math.Abs(a.Y-b.Y);
        System.Windows.Controls.Canvas.SetLeft(_selection,x); System.Windows.Controls.Canvas.SetTop(_selection,y); _selection.Width=w; _selection.Height=h;
    }
    private void Finish()
    {
        SelectedRegion=LongShotCaptureGeometry.FromPhysicalPoints(_startScreen,_endScreen);
        DialogResult = true; Close();
    }
    private System.Drawing.Point GetPhysicalCursor(System.Windows.Point fallback)
    {
        if(NativeMethods.GetCursorPos(out var cursor))return cursor;
        var screen=PointToScreen(fallback);return new((int)Math.Round(screen.X),(int)Math.Round(screen.Y));
    }
}
