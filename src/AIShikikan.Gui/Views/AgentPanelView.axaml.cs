using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AIShikikan.Gui.Views;

public partial class AgentPanelView : UserControl
{
    public AgentPanelView()
    {
        InitializeComponent();

        // Material 主题把过渡放在控件模板部件上, 仅在 XAML 中清空外层控件的
        // Transitions 不足以覆盖这些部件。容器完成模板实例化后再统一清理,
        // 确保删除 ItemsControl 项时不会在逻辑树分离阶段重新订阅过渡。
        SubAgentsList.ContainerPrepared += OnSubAgentContainerPrepared;
        ActualThemeVariantChanged += (_, _) =>
            Dispatcher.UIThread.Post(() => DisableTransitions(SubAgentsList), DispatcherPriority.Loaded);
    }

    private void OnSubAgentContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        DisableTransitions(e.Container);

        // ContentPresenter 的模板/子树可能在本事件之后才完成挂载, 再补一次
        // Loaded 优先级清理, 覆盖 Button/ToggleSwitch 的 Material 模板部件。
        Dispatcher.UIThread.Post(() => DisableTransitions(e.Container), DispatcherPriority.Loaded);
    }

    private static void DisableTransitions(Visual visual)
    {
        if (visual is Animatable animatable && animatable.Transitions is { Count: > 0 })
        {
            animatable.Transitions = null;
        }

        foreach (var child in visual.GetVisualChildren())
        {
            DisableTransitions(child);
        }
    }
}
