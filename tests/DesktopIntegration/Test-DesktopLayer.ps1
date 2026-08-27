param([Parameter(Mandatory=$true)][int]$TargetProcessId)

$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class GtoolsDesktopLayerProbe
{
    public delegate bool EnumProc(IntPtr hwnd,IntPtr value);
    [DllImport("user32.dll")]static extern bool EnumWindows(EnumProc callback,IntPtr value);
    [DllImport("user32.dll")]static extern bool EnumChildWindows(IntPtr parent,EnumProc callback,IntPtr value);
    [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint processId);
    [DllImport("user32.dll")]public static extern IntPtr GetWindow(IntPtr hwnd,uint command);
    [DllImport("user32.dll")]public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("dwmapi.dll")]static extern int DwmGetWindowAttribute(IntPtr hwnd,int attribute,out int value,int size);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr hwnd,StringBuilder text,int capacity);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]public static extern int GetClassName(IntPtr hwnd,StringBuilder text,int capacity);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]public static extern IntPtr FindWindowEx(IntPtr parent,IntPtr after,string className,string title);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]static extern IntPtr GetWindowLongPtr64(IntPtr hwnd,int index);
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")]static extern IntPtr GetWindowLong32(IntPtr hwnd,int index);

    public static IntPtr Find(int processId,string title)
    {
        IntPtr found=IntPtr.Zero;
        EnumProc inspect=(hwnd,value)=>
        {
            GetWindowThreadProcessId(hwnd,out var pid);
            var text=new StringBuilder(256);GetWindowText(hwnd,text,text.Capacity);
            if(pid==processId&&text.ToString()==title){found=hwnd;return false;}
            return true;
        };
        EnumWindows((top,value)=>
        {
            if(!inspect(top,value))return false;
            EnumChildWindows(top,(child,childValue)=>found==IntPtr.Zero&&inspect(child,childValue),value);
            return found==IntPtr.Zero;
        },IntPtr.Zero);
        return found;
    }

    public static long Style(IntPtr hwnd,int index)=>(IntPtr.Size==8?GetWindowLongPtr64(hwnd,index):GetWindowLong32(hwnd,index)).ToInt64();
    public static string Class(IntPtr hwnd){var text=new StringBuilder(128);GetClassName(hwnd,text,text.Capacity);return text.ToString();}
    public static bool IsCloaked(IntPtr hwnd){return DwmGetWindowAttribute(hwnd,14,out var value,sizeof(int))==0&&value!=0;}
}
'@

$window=[GtoolsDesktopLayerProbe]::Find($TargetProcessId,'Gtools 悬浮条')
if($window -eq [IntPtr]::Zero){throw '没有找到测试进程的 Gtools 悬浮条窗口。'}
$owner=[GtoolsDesktopLayerProbe]::GetWindow($window,4) # GW_OWNER
$previousOfOwner=[GtoolsDesktopLayerProbe]::GetWindow($owner,3) # GW_HWNDPREV
$style=[GtoolsDesktopLayerProbe]::Style($window,-16)
$exStyle=[GtoolsDesktopLayerProbe]::Style($window,-20)
$ownerClass=[GtoolsDesktopLayerProbe]::Class($owner)
$hasDefView=[GtoolsDesktopLayerProbe]::FindWindowEx($owner,[IntPtr]::Zero,'SHELLDLL_DefView',$null) -ne [IntPtr]::Zero
$visible=[GtoolsDesktopLayerProbe]::IsWindowVisible($window)
$cloaked=[GtoolsDesktopLayerProbe]::IsCloaked($window)
$result=[ordered]@{
    Passed=(($style -band 0x40000000) -eq 0 -and ($style -band 0x80000000L) -ne 0 -and ($exStyle -band 0x8) -eq 0 -and $hasDefView -and $ownerClass -in @('Progman','WorkerW') -and $previousOfOwner -eq $window -and $visible -and !$cloaked)
    WindowHandle=$window.ToInt64()
    OwnerHandle=$owner.ToInt64()
    OwnerClass=$ownerClass
    OwnerHostsShellDefView=$hasDefView
    IsImmediatelyAboveDesktop=($previousOfOwner -eq $window)
    IsVisible=$visible
    IsCloaked=$cloaked
    IsChild=(($style -band 0x40000000) -ne 0)
    IsPopup=(($style -band 0x80000000L) -ne 0)
    IsTopmost=(($exStyle -band 0x8) -ne 0)
}
$result | ConvertTo-Json
if(!$result.Passed){exit 1}
