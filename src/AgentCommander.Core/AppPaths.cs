using System.IO;
using System.Runtime.InteropServices;

namespace AgentCommander.Core;

public static class AppPaths
{
    private const string AppName = "Agent-Commander";
    private const string AppNameLower = "agent-commander";

    public static string ConfigDir { get; }
    public static string DataDir { get; }
    public static string CacheDir { get; }
    public static string LogDir { get; }

    public static string PreferencesPath { get; }
    public static string SessionsDir { get; }
    public static string BackgroundsDir { get; }

    public static string ProvidersPath { get; }
    public static string AgentsPath { get; }
    public static string PersonasDir { get; }
    public static string TemplatesDir { get; }
    public static string RosterTemplatePath { get; }
    public static string StepsDir { get; }
    public static string SubagentsDir { get; }
    public static string AssignmentsDir { get; }

    static AppPaths()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            ConfigDir = Path.Combine(appData, AppName, "Config");
            DataDir = Path.Combine(localAppData, AppName, "Data");
            CacheDir = Path.Combine(localAppData, AppName, "Cache");
            LogDir = Path.Combine(localAppData, AppName, "Logs");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            var appSupport = Path.Combine(home, "Library", "Application Support");

            ConfigDir = Path.Combine(appSupport, AppName, "Config");
            DataDir = Path.Combine(appSupport, AppName, "Data");
            CacheDir = Path.Combine(home, "Library", "Caches", AppName);
            LogDir = Path.Combine(appSupport, AppName, "Logs");
        }
        else
        {
            var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".config");
            var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".local", "share");
            var xdgCacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".cache");

            ConfigDir = Path.Combine(xdgConfigHome, AppNameLower);
            DataDir = Path.Combine(xdgDataHome, AppNameLower);
            CacheDir = Path.Combine(xdgCacheHome, AppNameLower);
            LogDir = Path.Combine(xdgDataHome, AppNameLower, "logs");
        }

        PreferencesPath = Path.Combine(ConfigDir, "preferences.json");
        SessionsDir = Path.Combine(DataDir, "sessions");
        BackgroundsDir = Path.Combine(DataDir, "backgrounds");

        ProvidersPath = Path.Combine(ConfigDir, "providers.json");
        AgentsPath = Path.Combine(ConfigDir, "agents.json");
        PersonasDir = Path.Combine(ConfigDir, "personas");
        TemplatesDir = Path.Combine(ConfigDir, "templates");
        RosterTemplatePath = Path.Combine(ConfigDir, "roster.prompt");
        StepsDir = Path.Combine(DataDir, "steps");
        SubagentsDir = Path.Combine(DataDir, "subagents");
        AssignmentsDir = Path.Combine(DataDir, "assignments");
    }

    public static void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(SessionsDir);
        Directory.CreateDirectory(BackgroundsDir);
        Directory.CreateDirectory(PersonasDir);
        Directory.CreateDirectory(TemplatesDir);
        Directory.CreateDirectory(StepsDir);
        Directory.CreateDirectory(SubagentsDir);
        Directory.CreateDirectory(AssignmentsDir);
    }
}
