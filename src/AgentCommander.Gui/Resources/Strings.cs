using System.Globalization;
using System.Resources;

namespace AgentCommander.Gui.Resources;

public static class Strings
{
    private static readonly ResourceManager _rm =
        new("AgentCommander.Gui.Resources.Strings", typeof(Strings).Assembly);

    private static CultureInfo _culture = CultureInfo.CurrentUICulture;

    public static CultureInfo Culture
    {
        get => _culture;
        set => _culture = value;
    }

    private static string Get(string name) => _rm.GetString(name, _culture) ?? name;

    public static string Nav_Home => Get(nameof(Nav_Home));
    public static string Nav_Chat => Get(nameof(Nav_Chat));
    public static string Nav_Settings => Get(nameof(Nav_Settings));
    public static string Home_Welcome => Get(nameof(Home_Welcome));
    public static string Home_Description => Get(nameof(Home_Description));
    public static string Home_QuickStart => Get(nameof(Home_QuickStart));
    public static string Home_QuickStartDesc => Get(nameof(Home_QuickStartDesc));
    public static string Home_ViewAgents => Get(nameof(Home_ViewAgents));
    public static string Home_ReadDocs => Get(nameof(Home_ReadDocs));
    public static string Settings_Title => Get(nameof(Settings_Title));
    public static string Settings_Appearance => Get(nameof(Settings_Appearance));
    public static string Settings_DarkTheme => Get(nameof(Settings_DarkTheme));
    public static string Settings_Language => Get(nameof(Settings_Language));
    public static string Settings_RestartHint => Get(nameof(Settings_RestartHint));
    public static string Settings_About => Get(nameof(Settings_About));
    public static string Settings_AppName => Get(nameof(Settings_AppName));
    public static string Settings_Version => Get(nameof(Settings_Version));
    public static string Settings_Font => Get(nameof(Settings_Font));
    public static string Settings_Background => Get(nameof(Settings_Background));
    public static string Settings_SelectBg => Get(nameof(Settings_SelectBg));
    public static string Settings_ClearBg => Get(nameof(Settings_ClearBg));
    public static string Theme_ToggleTip => Get(nameof(Theme_ToggleTip));
    public static string Nav_CollapseTip => Get(nameof(Nav_CollapseTip));
    public static string Chat_NewSession => Get(nameof(Chat_NewSession));
    public static string Chat_InputPlaceholder => Get(nameof(Chat_InputPlaceholder));
    public static string Chat_Send => Get(nameof(Chat_Send));
    public static string Chat_NoSessions => Get(nameof(Chat_NoSessions));
    public static string Chat_DefaultTitle => Get(nameof(Chat_DefaultTitle));
    public static string App_Title => Get(nameof(App_Title));
}
