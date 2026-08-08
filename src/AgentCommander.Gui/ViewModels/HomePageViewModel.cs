using AgentCommander.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentCommander.Gui.ViewModels;

public partial class HomePageViewModel : ViewModelBase
{
    public string WelcomeText { get; } = "欢迎使用 Agent Commander";
    public string DescriptionText { get; } = "一个强大的 Agent 管理与指挥工具";
    public string VersionText { get; } = $"版本 {AppInfo.Version}";
}
