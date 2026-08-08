namespace AgentCommander.Core;

public static class AppInfo
{
    public const string Name = "Agent-Commander";
    public const string Version = "1.0.0";

    public static string Describe() => $"{Name} v{Version}";
}