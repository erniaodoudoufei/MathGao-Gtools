using System.Diagnostics;
using System.Net.NetworkInformation;

namespace Gtools.App;

internal sealed class SystemMetrics
{
    // 超过这个间隔就认为采样断档（悬浮条被隐藏、机器休眠过），只重建基线不出数，
    // 否则会把几分钟的累计流量摊成"这一秒"的速率。
    private const double MaxSampleGap = 5;

    private ulong _idle, _kernel, _user;
    private readonly Dictionary<string,(ulong Rx,ulong Tx)> _nics = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _last = TimeSpan.Zero;

    public SystemSnapshot Sample()
    {
        NativeMethods.GetSystemTimes(out var i,out var k,out var u);
        var idle=i.Value; var kernel=k.Value; var user=u.Value;
        var total=(kernel-_kernel)+(user-_user); var idleDelta=idle-_idle;
        var cpu=_kernel==0||total==0?0:100d*(total-idleDelta)/total;
        _idle=idle;_kernel=kernel;_user=user;
        var mem=new NativeMethods.MEMORYSTATUSEX{Length=(uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()};
        NativeMethods.GlobalMemoryStatusEx(ref mem);

        var now=_clock.Elapsed;var raw=(now-_last).TotalSeconds;_last=now;
        var stale=raw<=0||raw>MaxSampleGap;var sec=Math.Max(.05,raw);
        // 逐块网卡各自求差：直接把各网卡的累计值加总再求差的话，虚拟网卡一上线
        // 就会把它的历史累计当成这一秒的增量，飙出假的 GB/s；掉线则整秒判零。
        ulong rxDelta=0,txDelta=0;var seen=new HashSet<string>();
        foreach(var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if(!Counts(n))continue;
            try
            {
                // GetIPStatistics 而不是 GetIPv4Statistics：后者不含 IPv6，
                // 在双栈网络（比如运营商下发公网 v6）下会漏掉大半流量。
                var s=n.GetIPStatistics();
                var rx=(ulong)Math.Max(0,s.BytesReceived);var tx=(ulong)Math.Max(0,s.BytesSent);
                seen.Add(n.Id);
                if(_nics.TryGetValue(n.Id,out var prev))
                {
                    if(rx>=prev.Rx)rxDelta+=rx-prev.Rx;
                    if(tx>=prev.Tx)txDelta+=tx-prev.Tx;
                }
                _nics[n.Id]=(rx,tx);
            }
            catch{}
        }
        foreach(var gone in _nics.Keys.Where(id=>!seen.Contains(id)).ToList())_nics.Remove(gone);

        return new SystemSnapshot(cpu,mem.MemoryLoad,stale?0:rxDelta/sec,stale?0:txDelta/sec);
    }

    // 只统计真正跑到外面去的网卡。隧道（Teredo/ISATAP/VPN）和 Hyper-V / VMware /
    // VirtualBox 的虚拟交换机都是叠在物理网卡之上的，一并计入会把同一份流量算两遍。
    private static bool Counts(NetworkInterface n)
    {
        if(n.OperationalStatus!=OperationalStatus.Up)return false;
        if(n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)return false;
        var d=n.Description;
        return !(d.Contains("Hyper-V Virtual Ethernet",StringComparison.OrdinalIgnoreCase)
            ||d.Contains("VMware Virtual Ethernet",StringComparison.OrdinalIgnoreCase)
            ||d.Contains("VirtualBox Host-Only",StringComparison.OrdinalIgnoreCase)
            ||d.Contains("Wi-Fi Direct",StringComparison.OrdinalIgnoreCase)
            ||d.Contains("Loopback",StringComparison.OrdinalIgnoreCase)
            ||d.Contains("Npcap",StringComparison.OrdinalIgnoreCase));
    }
}
internal sealed record SystemSnapshot(double Cpu,double Memory,double DownBytes,double UpBytes)
{
    public static string Rate(double b) => Gtools.Core.ByteRate.PerSecond(b);
}
