using System.Diagnostics;
using System.Reflection;

namespace Gtools.App;

internal sealed record RunningApplicationInfo(string DisplayVersion,string FileVersion,string ExecutablePath)
{
    public string DisplayLabel => $"当前版本 v{DisplayVersion}";
}

internal static class ApplicationIdentity
{
    private static readonly Lazy<RunningApplicationInfo> Cached = new(ReadCurrent);

    public static RunningApplicationInfo Current => Cached.Value;

    private static RunningApplicationInfo ReadCurrent()
    {
        var path=Environment.ProcessPath??Assembly.GetEntryAssembly()?.Location??"";
        string? fileVersion=null;
        try
        {
            if(!string.IsNullOrWhiteSpace(path)&&File.Exists(path))
                fileVersion=FileVersionInfo.GetVersionInfo(path).FileVersion;
        }
        catch { }
        var assemblyVersion=Assembly.GetEntryAssembly()?.GetName().Version?.ToString();
        return Resolve(path,fileVersion,assemblyVersion);
    }

    internal static RunningApplicationInfo Resolve(string? executablePath,string? fileVersion,string? assemblyVersion)
    {
        var full=NormalizeVersion(fileVersion)??NormalizeVersion(assemblyVersion)??"unknown";
        var display=Shorten(full);
        var path=string.IsNullOrWhiteSpace(executablePath)?"无法确定当前程序位置":Path.GetFullPath(executablePath);
        return new(display,full,path);
    }

    internal static string Shorten(string version)
    {
        if(!Version.TryParse(version,out var parsed))return version;
        if(parsed.Revision==0)return $"{parsed.Major}.{parsed.Minor}.{Math.Max(parsed.Build,0)}";
        return parsed.ToString();
    }

    private static string? NormalizeVersion(string? version)
    {
        if(string.IsNullOrWhiteSpace(version))return null;
        var clean=version.Split('+')[0].Trim();
        return Version.TryParse(clean,out var parsed)?parsed.ToString():clean;
    }
}
