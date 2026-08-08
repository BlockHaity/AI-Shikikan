using AgentCommander.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentCommander.Gui.ViewModels;

public partial class SettingsPageViewModel : ViewModelBase
{
    public string AppName { get; } = AppInfo.Name;
    public string AppVersion { get; } = AppInfo.Version;
    public string LicenseInfo { get; } = "MIT License";
    public string FontInfo { get; } = "HarmonyOS Sans SC";
}
