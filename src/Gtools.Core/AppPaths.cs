namespace Gtools.Core;

public static class AppPaths
{
    private static string? _rootOverride;
    private static bool _isolateOutputs;
    public static string Root => _rootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gtools");
    public static string Data => Ensure(Path.Combine(Root, "Data"));
    public static string Models => Ensure(Path.Combine(Root, "Models"));
    public static string Logs => Ensure(Path.Combine(Root, "Logs"));
    public static string Temp => Ensure(Path.Combine(Path.GetTempPath(), "Gtools"));
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string DatabaseFile => Path.Combine(Data, "gtools.db");
    public static string LongShots => Ensure(_isolateOutputs ? Path.Combine(Root,"LongShots") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Gtools", "LongShots"));
    public static void Initialize(string? dataRoot,bool isolateOutputs=false)
    {
        if(!string.IsNullOrWhiteSpace(dataRoot))
        {
            var full=Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataRoot));
            if(Path.GetPathRoot(full)==full)throw new ArgumentException("Gtools 数据目录不能是磁盘根目录。",nameof(dataRoot));
            _rootOverride=full;
        }
        _isolateOutputs=isolateOutputs;EnsureAll();
    }
    public static void EnsureAll() { Ensure(Root); _ = Data; _ = Models; _ = Logs; _ = Temp; _ = LongShots; }
    private static string Ensure(string path) { Directory.CreateDirectory(path); return path; }
}
