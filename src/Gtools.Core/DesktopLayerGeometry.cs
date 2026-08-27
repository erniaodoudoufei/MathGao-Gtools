namespace Gtools.Core;

/// <summary>
/// 桌面层的宿主、样式、Z-order 与 DPI 换算规则。
/// 集中为无 Win32 依赖的纯函数，便于在没有 Explorer 的测试进程中验证。
/// </summary>
internal static class DesktopLayerGeometry
{
    internal readonly record struct HostCandidate(nint Handle,string ClassName,bool HostsDefView);

    internal static nint SelectHost(IEnumerable<HostCandidate> candidates)
    {
        foreach(var candidate in candidates)
        {
            if(candidate.Handle==0||!candidate.HostsDefView)continue;
            if(candidate.ClassName.Equals("Progman",StringComparison.OrdinalIgnoreCase)||
               candidate.ClassName.Equals("WorkerW",StringComparison.OrdinalIgnoreCase))return candidate.Handle;
        }
        return 0;
    }

    internal static (long Style,long ExStyle) NormalizeOwnedPopupStyles(long style,long exStyle)
    {
        const long wsChild=0x40000000L,wsPopup=0x80000000L,wsExTopmost=0x8L;
        return ((style&~wsChild)|wsPopup,exStyle&~wsExTopmost);
    }

    /// <summary>返回 Z-order 插入参照；null 表示窗口已经紧贴在桌面宿主上方。</summary>
    internal static nint? ResolveDesktopInsertAfter(nint previousOfHost,nint dockWindow) =>
        previousOfHost==dockWindow?null:previousOfHost;

    internal static (int X,int Y) ScreenDipToHostClientPixels(double screenLeft,double screenTop,
        double dpiScaleX,double dpiScaleY,int hostScreenLeftPixels,int hostScreenTopPixels)
    {
        var x=ToPixel(screenLeft,dpiScaleX)-hostScreenLeftPixels;
        var y=ToPixel(screenTop,dpiScaleY)-hostScreenTopPixels;
        return (x,y);
    }

    internal static (double Left,double Top) ScreenPixelsToDip(int screenLeftPixels,int screenTopPixels,
        double dpiScaleX,double dpiScaleY) =>
        (screenLeftPixels/SafeScale(dpiScaleX),screenTopPixels/SafeScale(dpiScaleY));

    internal static (double X,double Y) PixelDeltaToDip(int deltaX,int deltaY,double dpiScaleX,double dpiScaleY) =>
        (deltaX/SafeScale(dpiScaleX),deltaY/SafeScale(dpiScaleY));

    private static int ToPixel(double value,double scale)=>(int)Math.Round(value*SafeScale(scale),MidpointRounding.AwayFromZero);
    private static double SafeScale(double scale)=>double.IsFinite(scale)&&scale>0?scale:1;
}
