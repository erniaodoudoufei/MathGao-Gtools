namespace Gtools.Core;

public static class ByteRate
{
    // 三位有效数字，和 TrafficMonitor 的显示习惯一致：9.98 KB/s、11.9 KB/s、150 KB/s、1.05 MB/s。
    public static string PerSecond(double bytesPerSecond) =>
        bytesPerSecond >= 1024 * 1024 ? $"{Significant(bytesPerSecond / 1024 / 1024)} MB/s" :
        bytesPerSecond >= 1024 ? $"{Significant(bytesPerSecond / 1024)} KB/s" :
        $"{Significant(bytesPerSecond)} B/s";

    private static string Significant(double value) =>
        value >= 100 ? value.ToString("0") : value >= 10 ? value.ToString("0.0") : value.ToString("0.00");
}
