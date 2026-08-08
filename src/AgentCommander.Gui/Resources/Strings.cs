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
    public static string Chat_ClearSession => Get(nameof(Chat_ClearSession));
    public static string Chat_PanelTitle => Get(nameof(Chat_PanelTitle));
    public static string Chat_Expert => Get(nameof(Chat_Expert));
    public static string Chat_ExpertDesc => Get(nameof(Chat_ExpertDesc));
    public static string Chat_Assign => Get(nameof(Chat_Assign));
    public static string Chat_AssignDesc => Get(nameof(Chat_AssignDesc));
    public static string Chat_AssignTaskWatermark => Get(nameof(Chat_AssignTaskWatermark));
    public static string Chat_AsyncMode => Get(nameof(Chat_AsyncMode));
    public static string Chat_StartAssign => Get(nameof(Chat_StartAssign));
    public static string Chat_Assignments => Get(nameof(Chat_Assignments));
    public static string Chat_Cancel => Get(nameof(Chat_Cancel));
    public static string Chat_Git => Get(nameof(Chat_Git));
    public static string Chat_Refresh => Get(nameof(Chat_Refresh));
    public static string Chat_GitMerge => Get(nameof(Chat_GitMerge));
    public static string Chat_GitDrop => Get(nameof(Chat_GitDrop));
    public static string Chat_GitRevert => Get(nameof(Chat_GitRevert));
    public static string Chat_GitDiff => Get(nameof(Chat_GitDiff));
    public static string App_Title => Get(nameof(App_Title));
}
