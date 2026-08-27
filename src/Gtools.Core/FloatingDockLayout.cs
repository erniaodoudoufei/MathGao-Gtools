namespace Gtools.Core;

public static class FloatingDockLayout
{
    /// <summary>
    /// 计算悬浮窗的落点。约定：设置里存的锚点是<b>最下面那一块的左上角</b>——
    /// 待办块拼在流量条上方，展开时窗口向上生长，下面的流量条必须原地不动。
    /// 所以窗口的 Top = 锚点 Y - 上方各块的总高度。
    /// 同时做可见区域钳制：改分辨率或拔掉外接屏之后，浮窗不能留在屏幕外。
    /// </summary>
    /// <param name="anchorLeft">锚点 X，NaN 表示从未放置过。</param>
    /// <param name="anchorTop">锚点 Y，即最下面那一块的顶边。</param>
    /// <param name="aboveHeight">锚点上方所有块的总高度（含块间距）。</param>
    public static (double Left, double Top) Resolve(double anchorLeft, double anchorTop, double aboveHeight,
        double totalWidth, double totalHeight, DesktopRect workArea)
    {
        var above = double.IsNaN(aboveHeight) || double.IsInfinity(aboveHeight) ? 0 : Math.Max(0, aboveHeight);

        double left, top;
        if (Unusable(anchorLeft) || Unusable(anchorTop))
        {
            // 从未放置过：贴右下角，和 MonitorPlacement 的默认位置保持一致。
            left = workArea.Right - totalWidth - 14;
            top = workArea.Bottom - totalHeight - 14;
        }
        else
        {
            left = anchorLeft;
            top = anchorTop - above;
        }

        return (Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - totalWidth)),
                Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - totalHeight)));
    }

    /// <summary>由窗口当前位置反推锚点，用于拖动结束后回写设置。</summary>
    public static (double Left, double Top) AnchorOf(double windowLeft, double windowTop, double aboveHeight)
    {
        var above = double.IsNaN(aboveHeight) || double.IsInfinity(aboveHeight) ? 0 : Math.Max(0, aboveHeight);
        return (windowLeft, windowTop + above);
    }

    private static bool Unusable(double value) => double.IsNaN(value) || double.IsInfinity(value);
}
