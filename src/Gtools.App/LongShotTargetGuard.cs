using System.Drawing;
using System.Runtime.InteropServices;
using Point=System.Drawing.Point;

namespace Gtools.App;

internal sealed class LongShotTargetGuard
{
    private readonly IntPtr _target;
    private readonly Rectangle _bounds,_region;
    private readonly uint _dpi,_pid;
    public LongShotTargetGuard(Rectangle region)
    {
        _region=region;_target=GetAncestor(WindowFromPoint(new Point(region.Left+region.Width/2,region.Top+region.Height/2)),2);
        if(_target==IntPtr.Zero)throw new InvalidOperationException("无法确定选区所属窗口，请重新框选消息区域。");
        GetWindowRect(_target,out var rect);_bounds=rect.Rectangle;_dpi=GetDpiForWindow(_target);GetWindowThreadProcessId(_target,out _pid);
    }
    public string? Check()
    {
        if(!IsWindow(_target)||!IsWindowVisible(_target)||IsIconic(_target))return "目标窗口已隐藏或关闭，请恢复后重试。";
        GetWindowRect(_target,out var rect);GetWindowThreadProcessId(_target,out var pid);
        if(!IsGeometryUnchanged(_bounds,rect.Rectangle,_dpi,GetDpiForWindow(_target))||pid!=_pid)
            return "目标窗口的位置、大小或显示比例已改变。请恢复到框选时的位置和大小，或取消后重新框选。";
        if(!rect.Rectangle.Contains(_region))return "选区超出目标窗口，请重新框选。";
        var blocked=false;
        EnumWindows((window,_)=>
        {
            if(window==_target)return false;
            if(!IsWindowVisible(window)||IsIconic(window))return true;
            if(DwmGetWindowAttribute(window,14,out var cloaked,sizeof(int))==0&&cloaked!=0)return true;
            if(GetWindowRect(window,out var box)&&box.Rectangle.IntersectsWith(_region)){blocked=true;return false;}
            return true;
        },IntPtr.Zero);
        return blocked?"消息区域被其他窗口遮挡。请露出微信消息区域后重试。":null;
    }
    internal static bool IsGeometryUnchanged(Rectangle before,Rectangle after,uint beforeDpi,uint afterDpi)=>before==after&&beforeDpi==afterDpi;
    internal static void PlaceWindow(IntPtr window,int x,int y)=>SetWindowPos(window,IntPtr.Zero,x,y,0,0,0x0001|0x0004|0x0010);
    [StructLayout(LayoutKind.Sequential)]private struct RECT{public int Left,Top,Right,Bottom;public Rectangle Rectangle=>Rectangle.FromLTRB(Left,Top,Right,Bottom);}
    private delegate bool EnumWindowProc(IntPtr window,IntPtr state);
    [DllImport("user32.dll")]private static extern bool EnumWindows(EnumWindowProc callback,IntPtr state);
    [DllImport("user32.dll")]private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")]private static extern IntPtr GetAncestor(IntPtr window,uint flags);
    [DllImport("user32.dll")]private static extern bool GetWindowRect(IntPtr window,out RECT rect);
    [DllImport("user32.dll")]private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("user32.dll")]private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("dwmapi.dll")]private static extern int DwmGetWindowAttribute(IntPtr window,int attribute,out int value,int size);
}
