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
    public static string App_Title => Get(nameof(App_Title));
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
    public static string Chat_TogglePanel => Get(nameof(Chat_TogglePanel));
}