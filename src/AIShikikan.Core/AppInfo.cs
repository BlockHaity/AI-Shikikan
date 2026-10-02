using System.Reflection;

namespace AIShikikan.Core;

public static class AppInfo
{
    public const string Name = "AI-Shikikan";

    public static string Version
    {
        get
        {
            var info = Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(info))
            {
                info = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            }
            return (info ?? "0.0.0").Split('+')[0];
        }
    }

    public static string Describe() => $"{Name} v{Version}";
}
