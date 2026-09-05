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

    private static Bitmap Grab(System.Drawing.Rectangle rect)
    {
        var bitmap = new Bitmap(rect.Width, rect.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, rect.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    internal static (byte[] Png,StabilitySample Sample) CaptureStableSample(System.Drawing.Rectangle rect)
    {
        using var bitmap=Grab(rect);
        var area=new System.Drawing.Rectangle(0,0,bitmap.Width,bitmap.Height);
        var locked=bitmap.LockBits(area,ImageLockMode.ReadOnly,System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        StabilitySample sample;
        try
        {
            var bytes=new byte[Math.Abs(locked.Stride)*bitmap.Height];
            System.Runtime.InteropServices.Marshal.Copy(locked.Scan0,bytes,0,bytes.Length);
            var gray=StabilitySample.ToGray(bytes,bitmap.Width,bitmap.Height,Math.Abs(locked.Stride));
            sample=StabilitySample.FromGray(gray,bitmap.Width,bitmap.Height,bitmap.Width);
        }
        finally{bitmap.UnlockBits(locked);}
        using var stream=new MemoryStream();bitmap.Save(stream,ImageFormat.Png);
        return(stream.ToArray(),sample);
    }

}

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
