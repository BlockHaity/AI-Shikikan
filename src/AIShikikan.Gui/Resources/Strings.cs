using System.Globalization;
using System.Resources;

namespace AIShikikan.Gui.Resources;

public static class Strings
{
    private static readonly ResourceManager _rm =
        new("AIShikikan.Gui.Resources.Strings", typeof(Strings).Assembly);

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
    public static string Theme_ToggleTip => Get(nameof(Theme_ToggleTip));
    public static string Nav_CollapseTip => Get(nameof(Nav_CollapseTip));
    public static string Chat_NewSession => Get(nameof(Chat_NewSession));
    public static string Chat_InputPlaceholder => Get(nameof(Chat_InputPlaceholder));
    public static string Chat_Send => Get(nameof(Chat_Send));
    public static string Chat_NoSessions => Get(nameof(Chat_NoSessions));
    public static string Chat_DefaultTitle => Get(nameof(Chat_DefaultTitle));
    public static string Chat_ClearSession => Get(nameof(Chat_ClearSession));
    public static string Chat_AssignTaskWatermark => Get(nameof(Chat_AssignTaskWatermark));
    public static string Chat_AsyncMode => Get(nameof(Chat_AsyncMode));
    public static string Chat_StartAssign => Get(nameof(Chat_StartAssign));
    public static string Chat_Assignments => Get(nameof(Chat_Assignments));
    public static string Chat_Cancel => Get(nameof(Chat_Cancel));
    public static string Chat_Refresh => Get(nameof(Chat_Refresh));
    public static string Chat_GitMerge => Get(nameof(Chat_GitMerge));
    public static string Chat_GitDrop => Get(nameof(Chat_GitDrop));
    public static string Chat_GitRevert => Get(nameof(Chat_GitRevert));
    public static string Chat_GitDiff => Get(nameof(Chat_GitDiff));
    public static string Chat_QuickAssign => Get(nameof(Chat_QuickAssign));
    public static string Chat_TogglePanel => Get(nameof(Chat_TogglePanel));
    public static string Panel_AssignTool => Get(nameof(Panel_AssignTool));
    public static string Panel_GitTool => Get(nameof(Panel_GitTool));
    public static string Panel_AssignToolTip => Get(nameof(Panel_AssignToolTip));
    public static string Panel_GitToolTip => Get(nameof(Panel_GitToolTip));
    public static string Panel_ToggleTip => Get(nameof(Panel_ToggleTip));
    public static string SubAgent_Title => Get(nameof(SubAgent_Title));
    public static string SubAgent_Add => Get(nameof(SubAgent_Add));
    public static string SubAgent_AddName => Get(nameof(SubAgent_AddName));
    public static string SubAgent_AddExec => Get(nameof(SubAgent_AddExec));
    public static string SubAgent_AddDesc => Get(nameof(SubAgent_AddDesc));
    public static string SubAgent_AddHint => Get(nameof(SubAgent_AddHint));
    public static string SubAgent_DescPlaceholder => Get(nameof(SubAgent_DescPlaceholder));
    public static string SubAgent_Expert => Get(nameof(SubAgent_Expert));
    public static string SubAgent_None => Get(nameof(SubAgent_None));
    public static string SubAgent_Save => Get(nameof(SubAgent_Save));
    public static string SubAgent_RemoveTip => Get(nameof(SubAgent_RemoveTip));
    public static string SubAgent_SessionOverride => Get(nameof(SubAgent_SessionOverride));
    public static string SubAgent_ToGlobal => Get(nameof(SubAgent_ToGlobal));
    public static string SubAgent_NoAgents => Get(nameof(SubAgent_NoAgents));
    public static string SubAgent_RosterSwitch => Get(nameof(SubAgent_RosterSwitch));
    public static string SubAgent_CommanderTitle => Get(nameof(SubAgent_CommanderTitle));
    public static string SubAgent_CommanderDesc => Get(nameof(SubAgent_CommanderDesc));
    public static string SubAgent_UseCommanderPersona => Get(nameof(SubAgent_UseCommanderPersona));
    public static string GitPanel_Changes => Get(nameof(GitPanel_Changes));
    public static string GitPanel_Stage => Get(nameof(GitPanel_Stage));
    public static string GitPanel_Unstage => Get(nameof(GitPanel_Unstage));
    public static string GitPanel_StageAll => Get(nameof(GitPanel_StageAll));
    public static string GitPanel_Commit => Get(nameof(GitPanel_Commit));
    public static string GitPanel_CommitMessage => Get(nameof(GitPanel_CommitMessage));
    public static string GitPanel_NoChanges => Get(nameof(GitPanel_NoChanges));
    public static string GitPanel_Branches => Get(nameof(GitPanel_Branches));
    public static string GitPanel_Switch => Get(nameof(GitPanel_Switch));
    public static string GitPanel_Pull => Get(nameof(GitPanel_Pull));
    public static string GitPanel_Push => Get(nameof(GitPanel_Push));
    public static string GitPanel_Steps => Get(nameof(GitPanel_Steps));
    public static string GitPanel_NotRepo => Get(nameof(GitPanel_NotRepo));
    public static string GitPanel_DiffTitle => Get(nameof(GitPanel_DiffTitle));
    public static string GitPanel_Graph => Get(nameof(GitPanel_Graph));
    public static string GitPanel_NoGraph => Get(nameof(GitPanel_NoGraph));
    public static string App_Title => Get(nameof(App_Title));
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
    public static string Settings_Providers => Get(nameof(Settings_Providers));
    public static string Settings_ProviderAdd => Get(nameof(Settings_ProviderAdd));
    public static string Settings_ProviderName => Get(nameof(Settings_ProviderName));
    public static string Settings_ProviderBaseUrl => Get(nameof(Settings_ProviderBaseUrl));
    public static string Settings_ProviderApiKey => Get(nameof(Settings_ProviderApiKey));
    public static string Settings_ProviderModel => Get(nameof(Settings_ProviderModel));
    public static string Settings_ProviderKind => Get(nameof(Settings_ProviderKind));
    public static string Settings_Activate => Get(nameof(Settings_Activate));
    public static string Settings_Remove => Get(nameof(Settings_Remove));
    public static string Settings_Add => Get(nameof(Settings_Add));
    public static string Settings_Agents => Get(nameof(Settings_Agents));
    public static string Settings_AgentName => Get(nameof(Settings_AgentName));
    public static string Settings_AgentExecutable => Get(nameof(Settings_AgentExecutable));
    public static string Settings_FontSelect => Get(nameof(Settings_FontSelect));
    public static string Settings_FontCustom => Get(nameof(Settings_FontCustom));
    public static string Settings_ModelsTitle => Get(nameof(Settings_ModelsTitle));
    public static string Settings_FetchModels => Get(nameof(Settings_FetchModels));
    public static string Settings_Fetching => Get(nameof(Settings_Fetching));
    public static string Settings_EnableAll => Get(nameof(Settings_EnableAll));
    public static string Settings_DisableAll => Get(nameof(Settings_DisableAll));
    public static string Settings_ProviderDefaultModel => Get(nameof(Settings_ProviderDefaultModel));
    public static string Settings_AgentSave => Get(nameof(Settings_AgentSave));
    public static string Settings_AgentEditHint => Get(nameof(Settings_AgentEditHint));
    public static string Chat_Model => Get(nameof(Chat_Model));
}