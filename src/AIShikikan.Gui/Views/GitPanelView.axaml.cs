using Avalonia.Controls;
using Avalonia.Platform.Storage;
using AIShikikan.Gui.Resources;
using AIShikikan.Gui.ViewModels;

namespace AIShikikan.Gui.Views;

public partial class GitPanelView : UserControl
{
    public GitPanelView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is GitPanelViewModel vm)
        {
            vm.FolderPicker = PickFolderAsync;
        }
    }

    private async Task<string?> PickFolderAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.GitPanel_SelectFolder,
            AllowMultiple = false
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
