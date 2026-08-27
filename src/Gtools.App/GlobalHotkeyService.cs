using System.Windows.Input;
using System.Windows.Interop;

namespace Gtools.App;

internal sealed class GlobalHotkeyService : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 100;
    public GlobalHotkeyService()
    {
        _source = new HwndSource(new HwndSourceParameters("Gtools.MessageWindow")
        { Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000) });
        _source.AddHook(WndProc);
    }
    public IntPtr Handle => _source.Handle;
    public bool Register(string text, Action action, out string? error)
    {
        error = null;
        if (!TryParse(text, out var modifiers, out var key)) { error = $"无法解析快捷键：{text}"; return false; }
        var id = _nextId++;
        if (!NativeMethods.RegisterHotKey(_source.Handle, id, modifiers | NativeMethods.MOD_NOREPEAT, (uint)KeyInterop.VirtualKeyFromKey(key)))
        { error = $"快捷键已被占用：{text}"; return false; }
        _actions[id] = action; return true;
    }
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        { handled = true; action(); }
        return IntPtr.Zero;
    }
    private static bool TryParse(string value, out uint mods, out Key key)
    {
        mods = 0; key = Key.None;
        foreach (var part in value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) mods |= NativeMethods.MOD_ALT;
            else if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase)) mods |= NativeMethods.MOD_CONTROL;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) mods |= NativeMethods.MOD_SHIFT;
            else if (!Enum.TryParse(part, true, out key)) return false;
        }
        return key != Key.None;
    }
    public void Dispose()
    {
        foreach (var id in _actions.Keys) NativeMethods.UnregisterHotKey(_source.Handle, id);
        _source.RemoveHook(WndProc); _source.Dispose();
    }
}
