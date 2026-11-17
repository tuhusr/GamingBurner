using System.IO;
using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace GamingBurner;

/// <summary>Where GamingBurner keeps its files, so it works the same on any PC.</summary>
public static class AppPaths
{
    /// <summary>The folder the app runs from (holds the latency engine and licence files).</summary>
    public static readonly string AppDir = AppContext.BaseDirectory;

    /// <summary>Settings, per Windows user. Outside the install folder so updates never touch them.</summary>
    public static readonly string SettingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GamingBurner");
    public static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    /// <summary>Recordings and logs, in Documents so they are easy to find and share.</summary>
    public static readonly string LogsDir = Directory.Exists(@"C:\GamingBurner\Logs")
        ? @"C:\GamingBurner\Logs" // the original development copy keeps its existing logs where they are
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GamingBurner", "Logs");

    public static readonly string EngineDir = Path.Combine(AppDir, "Engine");

    /// <summary>Moves settings from the original development location, once.</summary>
    public static void MigrateOldSettings()
    {
        try
        {
            const string old = @"C:\GamingBurner\settings.json";
            Directory.CreateDirectory(SettingsDir);
            if (!File.Exists(SettingsFile) && File.Exists(old)) File.Copy(old, SettingsFile);
        }
        catch { }
    }
}

/// <summary>
/// Checks GitHub Releases for a newer version and installs it. Uses Velopack, which also built
/// the installer; updates only download the parts that changed.
/// </summary>
public static class Updates
{
    public const string RepoUrl = "https://github.com/tuhusr/GamingBurner";

    static UpdateManager _mgr;

    static UpdateManager Mgr
    {
        get
        {
            if (_mgr == null)
            {
                try
                {
                    // GAMINGBURNER_UPDATE_FEED points at a local folder of releases; used only to test updating without publishing.
                    string feed = Environment.GetEnvironmentVariable("GAMINGBURNER_UPDATE_FEED");
                    _mgr = string.IsNullOrEmpty(feed) ? new UpdateManager(new GithubSource(RepoUrl, null, false, null)) : new UpdateManager(feed);
                }
                catch { }
            }
            return _mgr;
        }
    }

    /// <summary>False when running a development build that was not installed with Setup.exe.</summary>
    public static bool Installed { get { try { return Mgr?.IsInstalled == true; } catch { return false; } } }

    public static string CurrentVersion
    {
        get
        {
            try { if (Installed && Mgr.CurrentVersion != null) return Mgr.CurrentVersion.ToString(); } catch { }
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>The newer release, or null when up to date or the check could not run.</summary>
    public static async Task<UpdateInfo> CheckAsync()
    {
        if (!Installed) return null;
        return await Mgr.CheckForUpdatesAsync().ConfigureAwait(false);
    }

    public static async Task DownloadAsync(UpdateInfo info, Action<int> progress)
    {
        await Mgr.DownloadUpdatesAsync(info, progress).ConfigureAwait(false);
    }

    /// <summary>Hands the downloaded update to Velopack, which installs it once the app has closed and then reopens it.</summary>
    public static void ApplyAfterExit(UpdateInfo info, string[] restartArgs = null)
    {
        Mgr.WaitExitThenApplyUpdates(info.TargetFullRelease, false, true, restartArgs ?? Array.Empty<string>());
    }
}
