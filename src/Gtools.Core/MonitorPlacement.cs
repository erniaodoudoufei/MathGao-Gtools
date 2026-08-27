namespace Gtools.Core;

public static class MonitorPlacement
{
    /// <summary>
    /// 决定性能悬浮条的落点。没有记录过位置就贴右下角；
    /// 记录过但已经落在可见区域之外（拔掉了外接屏、改了分辨率）也要拉回来，否则悬浮条会消失。
    /// </summary>
    public static (double Left, double Top) Resolve(double savedLeft, double savedTop, DesktopRect workArea, double width, double height)
    {
        var right = workArea.Right - width - 14;
        var bottom = workArea.Bottom - height - 14;
        if (double.IsNaN(savedLeft) || double.IsNaN(savedTop) || double.IsInfinity(savedLeft) || double.IsInfinity(savedTop))
            return (right, bottom);
        var visibleLeft = Math.Clamp(savedLeft, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
        var visibleTop = Math.Clamp(savedTop, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
        return (visibleLeft, visibleTop);
    }
}
