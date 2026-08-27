using System.Runtime.InteropServices;

namespace Gtools.App;

internal static class NativeMethods
{
    internal const int WM_HOTKEY = 0x0312;
    internal const int WM_CLIPBOARDUPDATE = 0x031D;
    internal const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_NOREPEAT = 0x4000;
    internal const int SW_RESTORE = 9;

    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] internal static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out System.Drawing.Point point);
    [DllImport("user32.dll")] internal static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extraInfo);
    [DllImport("user32.dll")] internal static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("kernel32.dll")] internal static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
    [DllImport("kernel32.dll")] internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX state);

    [StructLayout(LayoutKind.Sequential)] internal struct FILETIME { public uint Low, High; public ulong Value => ((ulong)High << 32) | Low; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)] internal struct MEMORYSTATUSEX
    {
        public uint Length; public uint MemoryLoad; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct INPUT { public uint Type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion { [FieldOffset(0)] public KEYBDINPUT Keyboard; }
    [StructLayout(LayoutKind.Sequential)] internal struct KEYBDINPUT { public ushort Vk, Scan; public uint Flags, Time; public UIntPtr ExtraInfo; }

    internal static void Paste()
    {
        const ushort VK_CONTROL = 0x11, VK_V = 0x56; const uint KEYUP = 0x0002;
        var a = new[]
        {
            Key(VK_CONTROL,0), Key(VK_V,0), Key(VK_V,KEYUP), Key(VK_CONTROL,KEYUP)
        };
        SendInput((uint)a.Length, a, Marshal.SizeOf<INPUT>());
    }
    private static INPUT Key(ushort vk, uint flags) => new() { Type = 1, U = new InputUnion { Keyboard = new KEYBDINPUT { Vk = vk, Flags = flags } } };
}
