using System.Net.NetworkInformation;

namespace Gtools.App;

internal sealed class SystemMetrics
{
    private ulong _idle, _kernel, _user, _rx, _tx;
    private DateTime _last = DateTime.UtcNow;
    public SystemSnapshot Sample()
    {
        NativeMethods.GetSystemTimes(out var i,out var k,out var u);
        var idle=i.Value; var kernel=k.Value; var user=u.Value;
        var total=(kernel-_kernel)+(user-_user); var idleDelta=idle-_idle;
        var cpu=_kernel==0||total==0?0:100d*(total-idleDelta)/total;
        _idle=idle;_kernel=kernel;_user=user;
        var mem=new NativeMethods.MEMORYSTATUSEX{Length=(uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()};
        NativeMethods.GlobalMemoryStatusEx(ref mem);
        ulong rx=0,tx=0;
        foreach(var n in NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up && n.NetworkInterfaceType!=NetworkInterfaceType.Loopback))
        { try { var s=n.GetIPv4Statistics();rx+=(ulong)s.BytesReceived;tx+=(ulong)s.BytesSent;}catch{} }
        var now=DateTime.UtcNow;var sec=Math.Max(.05,(now-_last).TotalSeconds);
        var result=new SystemSnapshot(cpu,mem.MemoryLoad,_rx==0||rx<_rx?0:(rx-_rx)/sec,_tx==0||tx<_tx?0:(tx-_tx)/sec);
        _rx=rx;_tx=tx;_last=now;return result;
    }
}
internal sealed record SystemSnapshot(double Cpu,double Memory,double DownBytes,double UpBytes)
{
    public static string Rate(double b) => Gtools.Core.ByteRate.PerSecond(b);
}
