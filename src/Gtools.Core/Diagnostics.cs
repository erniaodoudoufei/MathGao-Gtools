using System.Text.Json;

namespace Gtools.Core;

public sealed record AppRuntimeOptions(string? DataRoot,bool IsTestProfile,bool Diagnostics,bool ShowDashboard,bool SelfTest,
    string? ReportPath,LongShotCaptureProfile? CaptureProfile)
{
    public static AppRuntimeOptions Parse(string[] args)
    {
        string? Value(string key){var i=Array.FindIndex(args,x=>x.Equals(key,StringComparison.OrdinalIgnoreCase));return i>=0&&i+1<args.Length?args[i+1]:null;}
        var root=Value("--test-profile")??Value("--data-root");LongShotCaptureProfile? profile=Enum.TryParse<LongShotCaptureProfile>(Value("--capture-profile"),true,out var p)?p:null;
        return new(root,args.Contains("--test-profile",StringComparer.OrdinalIgnoreCase),args.Contains("--diagnostics",StringComparer.OrdinalIgnoreCase),
            args.Contains("--show-dashboard",StringComparer.OrdinalIgnoreCase),args.Contains("--self-test",StringComparer.OrdinalIgnoreCase),Value("--report"),profile);
    }
}

public sealed record DiagnosticEvent(DateTimeOffset Timestamp,string Level,string Component,string EventName,string Message,
    string? RequestId=null,IReadOnlyDictionary<string,object?>? Data=null);

public static class DiagnosticLog
{
    private static readonly object Gate=new();private static string? _path;public static bool Enabled=>_path is not null;
    // 日志文件是 .jsonl，一行一条记录。GtoolsJson.Options 带 WriteIndented（settings.json 需要），
    // 直接拿来写日志会让每条记录跨多行，文件就不再是 JSONL，任何按行解析的工具都读不了。
    private static readonly JsonSerializerOptions LineOptions=new(GtoolsJson.Options){WriteIndented=false};
    public static void Initialize(bool enabled,string? file=null)
    {
        if(!enabled){_path=null;return;}AppPaths.EnsureAll();_path=file??Path.Combine(AppPaths.Logs,$"gtools-{DateTime.Now:yyyyMMdd}.jsonl");
    }
    public static void Write(string component,string eventName,string message,string level="Info",string? requestId=null,IReadOnlyDictionary<string,object?>? data=null)
    {
        if(_path is null)return;try{var line=JsonSerializer.Serialize(new DiagnosticEvent(DateTimeOffset.Now,level,component,eventName,message,requestId,data),LineOptions);lock(Gate)File.AppendAllText(_path,line+Environment.NewLine);}catch{}
    }
    public static string? CurrentPath=>_path;
}

public sealed record AcceptanceCheck(string Id,string Name,bool Passed,string Details,TimeSpan Duration);
public sealed record AcceptanceReport(string ProductVersion,string Profile,DateTimeOffset StartedAt,DateTimeOffset CompletedAt,
    bool Passed,AcceptanceCheck[] Checks,string? DiagnosticsLog,string[] KnownLimitations);
