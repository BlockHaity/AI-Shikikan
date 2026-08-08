using AIShikikan.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIShikikan.Gui.ViewModels;

public partial class HomePageViewModel : ViewModelBase
{
    public string WelcomeText { get; } = "欢迎使用 AI-Shikikan";
    public string DescriptionText { get; } = "一个强大的 Agent 管理与指挥工具";
    public string VersionText { get; } = $"版本 {AppInfo.Version}";
}
