using System.Reflection;

namespace CELMapViewer;

/// <summary>Product branding and the per-user data folder (formerly "MT_Zenith", before that "CEL Map Viewer").</summary>
public static class AppInfo
{
    public const string Name = "Topo";
#if TOPO_AI
    public const string Edition = "Topo + AI";
    public const bool HasAi = true;
#else
    public const string Edition = "Topo";
    public const bool HasAi = false;
#endif
    /// <summary>Window title: the edition, so it's always clear whether this build contains AI features.</summary>
    public const string Title = Edition;
    public const string McpName = "topo";

    public static string Version { get; } =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    private static string? _dataDir;

    /// <summary>Folders earlier versions kept their data in, newest first.</summary>
    private static readonly string[] FormerDataDirs = { "MT_Zenith", "CELMapViewer" };

    /// <summary>
    /// %APPDATA%\Topo. On first use it copies over the settings, presets, custom PCG models and the agent's placement memory
    /// from the folder of the newest earlier version found (%APPDATA%\MT_Zenith, else %APPDATA%\CELMapViewer). The old folder is left alone.
    /// </summary>
    public static string DataDir
    {
        get
        {
            if (_dataDir != null) return _dataDir;
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, Name);
            try
            {
                if (!Directory.Exists(dir) && FormerDataDirs.Select(n => Path.Combine(appData, n)).FirstOrDefault(Directory.Exists) is { } old)
                    CopyData(old, dir);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return _dataDir = dir;
        }
    }

    /// <summary>Copies a data folder with its subfolders, except the download cache (it is rebuilt on demand).</summary>
    private static void CopyData(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (string d in Directory.EnumerateDirectories(from))
            if (!Path.GetFileName(d).Equals("cache", StringComparison.OrdinalIgnoreCase))
                CopyData(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /// <summary>The popstar berry icon embedded in the exe.</summary>
    public static Icon? LoadIcon()
    {
        try
        {
            using var s = typeof(AppInfo).Assembly.GetManifestResourceStream("Topo.ico");
            return s == null ? null : new Icon(s);
        }
        catch (ArgumentException) { return null; }
    }
}
