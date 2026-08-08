using Avalonia.Controls;
using Avalonia.Input;
using AgentCommander.Gui.ViewModels;

namespace AgentCommander.Gui.Views;

public partial class ChatPageView : UserControl
{
    public ChatPageView()
    {
        InitializeComponent();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (DataContext is ChatPageViewModel vm && vm.SendMessageCommand.CanExecute(null))
            {
                vm.SendMessageCommand.Execute(null);
            }

            e.Handled = true;
        }
    }
}
