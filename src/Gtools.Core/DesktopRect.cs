namespace Gtools.Core;

public readonly record struct DesktopRect(int Left,int Top,int Right,int Bottom)
{
    public int Width=>Right-Left;
    public int Height=>Bottom-Top;
    public bool Contains(int x,int y)=>x>=Left&&x<Right&&y>=Top&&y<Bottom;
    public DesktopRect Union(DesktopRect other)=>new(
        Math.Min(Left,other.Left),Math.Min(Top,other.Top),Math.Max(Right,other.Right),Math.Max(Bottom,other.Bottom));
}
