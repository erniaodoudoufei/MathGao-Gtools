using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Gtools.Core;

namespace Gtools.App;

/// <summary>
/// 让 WPF 悬浮窗成为桌面宿主拥有的顶层工具窗口，并把它固定在桌面宿主正上方。
/// 不能把透明 WPF Window 转成 Explorer 的跨进程子窗口：句柄和层级虽然有效，
/// 但部分 Windows 11 / DWM 组合不会合成任何像素。
/// </summary>
internal sealed class DesktopLayerHost : IDisposable
{
    private const int GwlStyle=-16,GwlExStyle=-20,GwlHwndParent=-8;
    private const uint SwpNoSize=0x0001,SwpNoMove=0x0002,SwpNoZOrder=0x0004,SwpNoActivate=0x0010,SwpFrameChanged=0x0020,SwpNoOwnerZOrder=0x0200;
    private const int WmDisplayChange=0x007e,WmDpiChanged=0x02e0;
    private const uint GwHwndPrev=3,GwOwner=4;
    private static readonly nint HwndNoTopmost=-2;

    private readonly Window _window;
    private HwndSource? _source;
    private HwndSource? _broadcastSource;
    private nint _windowHandle,_hostHandle;
    private nint _originalStyle,_originalExStyle,_originalOwner;
    private uint _taskbarCreated;
    private bool _initialized,_disposed,_reattachQueued;

    internal DesktopLayerHost(Window window)=>_window=window;
    internal bool IsAttached=>_hostHandle!=0&&Native.IsWindow(_hostHandle)&&Native.GetWindow(_windowHandle,GwOwner)==_hostHandle;
    internal event EventHandler? PlacementInvalidated;

    internal void Initialize()
    {
        if(_initialized||_disposed)return;
        _windowHandle=new WindowInteropHelper(_window).Handle;
        if(_windowHandle==0)throw new InvalidOperationException("悬浮窗句柄尚未建立。");
        _source=HwndSource.FromHwnd(_windowHandle);
        _source?.AddHook(WindowProc);
        _originalStyle=Native.GetWindowLongPtr(_windowHandle,GwlStyle);
        _originalExStyle=Native.GetWindowLongPtr(_windowHandle,GwlExStyle);
        _originalOwner=Native.GetWindow(_windowHandle,GwOwner);
        _taskbarCreated=Native.RegisterWindowMessage("TaskbarCreated");
        // Explorer 重启时需要一个不依赖桌面 owner 生命周期的接收窗口。
        _broadcastSource=new HwndSource(new HwndSourceParameters("Gtools.DesktopLayerWatcher")
        {
            Width=0,Height=0,PositionX=-32000,PositionY=-32000,
            WindowStyle=0,ExtendedWindowStyle=0x00000080 // WS_EX_TOOLWINDOW
        });
        _broadcastSource.AddHook(WindowProc);
        _initialized=true;
        EnsureAttached();
    }

    internal bool EnsureAttached()
    {
        if(!_initialized||_disposed)return false;
        if(IsAttached)
        {
            NormalizeWindowStyles();
            PlaceImmediatelyAboveDesktop();
            return true;
        }

        var host=FindDesktopHost();
        if(host==0)
        {
            FallbackToNormal("没有找到承载桌面图标的 Progman/WorkerW");
            return false;
        }

        try
        {
            NormalizeWindowStyles();
            Native.SetLastError(0);
            Native.SetWindowLongPtr(_windowHandle,GwlHwndParent,host);
            if(Native.GetWindow(_windowHandle,GwOwner)!=host)
                throw new Win32Exception(Marshal.GetLastWin32Error(),"无法把悬浮窗 owner 关联到桌面宿主");
            _hostHandle=host;
            Native.SetWindowPos(_windowHandle,HwndNoTopmost,0,0,0,0,
                SwpNoMove|SwpNoSize|SwpNoActivate|SwpNoOwnerZOrder|SwpFrameChanged);
            PlaceImmediatelyAboveDesktop();
            DiagnosticLog.Write("desktop-layer","owner.attached",$"悬浮窗已关联到 {ClassName(host)} 桌面层",data:new Dictionary<string,object?>
            {
                ["window"]=_windowHandle.ToInt64(),["owner"]=host.ToInt64(),["ownerClass"]=ClassName(host),["mode"]="owned-popup"
            });
            return true;
        }
        catch(Exception ex)
        {
            FallbackToNormal(ex.Message);
            return false;
        }
    }

    /// <summary>窗口被显示或激活后 WPF 可能把它提升，立刻放回桌面宿主正上方。</summary>
    internal void ReassertDesktopLayer()
    {
        if(!_initialized||_disposed)return;
        EnsureAttached();
    }

    internal void MoveToScreenDip(double left,double top)
    {
        if(!_initialized||_disposed)return;
        // WPF 的 owned popup 在 SourceInitialized/Loaded 期间只修改 Left/Top 时，
        // Explorer 或 DWM 仍可能把透明 HWND 留在 (0,0)。先同步 WPF 属性，再把
        // 最终坐标直接提交给真实句柄；后者才是屏幕上实际绘制的位置。
        _window.Left=left;_window.Top=top;
        var (scaleX,scaleY)=DpiScale();
        var (x,y)=DesktopLayerGeometry.ScreenDipToPixels(left,top,scaleX,scaleY);
        if(_windowHandle!=0)
            Native.SetWindowPos(_windowHandle,0,x,y,0,0,
                SwpNoSize|SwpNoZOrder|SwpNoActivate|SwpNoOwnerZOrder);
    }

    internal (double Left,double Top) GetScreenDipPosition()
    {
        // Window.Left/Top 可能只是 WPF 希望的位置；桌面 owner 切换期间它与 HWND
        // 的真实坐标会短暂不同。持久化时必须以屏幕上实际窗口为准。
        if(_windowHandle!=0&&Native.GetWindowRect(_windowHandle,out var rect))
        {
            var (scaleX,scaleY)=DpiScale();
            return DesktopLayerGeometry.ScreenPixelsToDip(rect.Left,rect.Top,scaleX,scaleY);
        }
        if(double.IsFinite(_window.Left)&&double.IsFinite(_window.Top))return (_window.Left,_window.Top);
        return (0,0);
    }

    internal (double X,double Y) PixelDeltaToDip(int x,int y)
    {
        var (scaleX,scaleY)=DpiScale();
        return DesktopLayerGeometry.PixelDeltaToDip(x,y,scaleX,scaleY);
    }

    private void PlaceImmediatelyAboveDesktop()
    {
        if(!IsAttached)return;
        var previous=Native.GetWindow(_hostHandle,GwHwndPrev);
        var insertAfter=DesktopLayerGeometry.ResolveDesktopInsertAfter(previous,_windowHandle);
        if(insertAfter is null)return;
        Native.SetWindowPos(_windowHandle,insertAfter.Value,0,0,0,0,
            SwpNoMove|SwpNoSize|SwpNoActivate|SwpNoOwnerZOrder);
    }

    private void FallbackToNormal(string reason)
    {
        if(_windowHandle!=0)
        {
            Native.SetWindowLongPtr(_windowHandle,GwlHwndParent,0);
            NormalizeWindowStyles();
            Native.SetWindowPos(_windowHandle,HwndNoTopmost,0,0,0,0,
                SwpNoMove|SwpNoSize|SwpNoActivate|SwpNoOwnerZOrder|SwpFrameChanged);
        }
        _hostHandle=0;
        DiagnosticLog.Write("desktop-layer","owner.unavailable",reason,"Warning");
    }

    private void NormalizeWindowStyles()
    {
        if(_windowHandle==0)return;
        var normalized=DesktopLayerGeometry.NormalizeOwnedPopupStyles(
            Native.GetWindowLongPtr(_windowHandle,GwlStyle).ToInt64(),Native.GetWindowLongPtr(_windowHandle,GwlExStyle).ToInt64());
        Native.SetWindowLongPtr(_windowHandle,GwlStyle,unchecked((nint)normalized.Style));
        Native.SetWindowLongPtr(_windowHandle,GwlExStyle,unchecked((nint)normalized.ExStyle));
    }

    private void ScheduleReattach(string reason)
    {
        if(_reattachQueued||_disposed)return;
        _reattachQueued=true;
        _window.Dispatcher.BeginInvoke(() =>
        {
            _reattachQueued=false;
            DiagnosticLog.Write("desktop-layer","owner.refresh",reason);
            EnsureAttached();
            PlacementInvalidated?.Invoke(this,EventArgs.Empty);
        });
    }

    private nint WindowProc(nint hwnd,int message,nint wParam,nint lParam,ref bool handled)
    {
        if((_taskbarCreated!=0&&message==_taskbarCreated)||message==WmDisplayChange||message==WmDpiChanged)
            ScheduleReattach(message==WmDpiChanged?"DPI 已变化":message==WmDisplayChange?"显示器配置已变化":"Explorer 已重新建立任务栏");
        return 0;
    }

    private static nint FindDesktopHost()
    {
        var candidates=new List<DesktopLayerGeometry.HostCandidate>();
        Native.EnumWindows((window,_)=>
        {
            var className=ClassName(window);
            var defView=Native.FindWindowEx(window,0,"SHELLDLL_DefView",null);
            candidates.Add(new DesktopLayerGeometry.HostCandidate(window,className,defView!=0));
            return true;
        },0);
        return DesktopLayerGeometry.SelectHost(candidates);
    }

    private (double X,double Y) DpiScale()
    {
        var dpi=VisualTreeHelper.GetDpi(_window);
        if(dpi.DpiScaleX>0&&dpi.DpiScaleY>0)return (dpi.DpiScaleX,dpi.DpiScaleY);
        var raw=_windowHandle==0?96u:Native.GetDpiForWindow(_windowHandle);
        var scale=raw>0?raw/96d:1d;return (scale,scale);
    }

    private static string ClassName(nint hwnd)
    {
        var text=new StringBuilder(64);return Native.GetClassName(hwnd,text,text.Capacity)>0?text.ToString():"unknown";
    }

    public void Dispose()
    {
        if(_disposed)return;_disposed=true;
        _source?.RemoveHook(WindowProc);
        if(_broadcastSource is not null){_broadcastSource.RemoveHook(WindowProc);_broadcastSource.Dispose();}
        if(_windowHandle!=0)
        {
            Native.SetWindowLongPtr(_windowHandle,GwlHwndParent,_originalOwner);
            Native.SetWindowLongPtr(_windowHandle,GwlStyle,_originalStyle);
            var restored=DesktopLayerGeometry.NormalizeOwnedPopupStyles(_originalStyle.ToInt64(),_originalExStyle.ToInt64());
            Native.SetWindowLongPtr(_windowHandle,GwlExStyle,unchecked((nint)restored.ExStyle));
        }
        _source=null;_broadcastSource=null;_hostHandle=0;
    }

    private static class Native
    {
        internal delegate bool EnumWindowsProc(nint hwnd,nint lParam);
        [StructLayout(LayoutKind.Sequential)]internal struct Rect{internal int Left,Top,Right,Bottom;}

        [DllImport("user32.dll",SetLastError=true)]internal static extern bool EnumWindows(EnumWindowsProc callback,nint lParam);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]internal static extern nint FindWindowEx(nint parent,nint after,string className,string? title);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]internal static extern int GetClassName(nint hwnd,StringBuilder value,int capacity);
        [DllImport("user32.dll")]internal static extern nint GetWindow(nint hwnd,uint command);
        [DllImport("user32.dll")]internal static extern bool IsWindow(nint hwnd);
        [DllImport("user32.dll",SetLastError=true)]internal static extern bool SetWindowPos(nint hwnd,nint insertAfter,int x,int y,int width,int height,uint flags);
        [DllImport("user32.dll")]internal static extern bool GetWindowRect(nint hwnd,out Rect rect);
        [DllImport("user32.dll")]internal static extern uint RegisterWindowMessage(string message);
        [DllImport("user32.dll")]internal static extern uint GetDpiForWindow(nint hwnd);
        [DllImport("kernel32.dll")]internal static extern void SetLastError(uint error);

        internal static nint GetWindowLongPtr(nint hwnd,int index)=>IntPtr.Size==8?GetWindowLongPtr64(hwnd,index):GetWindowLong32(hwnd,index);
        internal static nint SetWindowLongPtr(nint hwnd,int index,nint value)=>IntPtr.Size==8?SetWindowLongPtr64(hwnd,index,value):SetWindowLong32(hwnd,index,value);
        [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]private static extern nint GetWindowLongPtr64(nint hwnd,int index);
        [DllImport("user32.dll",EntryPoint="GetWindowLongW")]private static extern nint GetWindowLong32(nint hwnd,int index);
        [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW",SetLastError=true)]private static extern nint SetWindowLongPtr64(nint hwnd,int index,nint value);
        [DllImport("user32.dll",EntryPoint="SetWindowLongW",SetLastError=true)]private static extern nint SetWindowLong32(nint hwnd,int index,nint value);
    }
}
