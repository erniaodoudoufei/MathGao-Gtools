namespace Gtools.Core;

internal static class LongShotCaptureGeometry
{
    public static System.Drawing.Rectangle FromPhysicalPoints(System.Drawing.Point first,System.Drawing.Point second)
    {
        var left=Math.Min(first.X,second.X);var top=Math.Min(first.Y,second.Y);
        return new(left,top,Math.Max(1,Math.Abs(first.X-second.X)),Math.Max(1,Math.Abs(first.Y-second.Y)));
    }
}

internal static class LongShotOutputNaming
{
    public static string CreateFileName(DateTime timestamp,string requestId)
    {
        var safeId=new string(requestId.Where(char.IsLetterOrDigit).Take(8).ToArray());
        if(safeId.Length==0)safeId=Guid.NewGuid().ToString("N")[..8];
        return $"Gtools-LongShot-{timestamp:yyyyMMdd-HHmmss-fff}-{safeId}.png";
    }
}
