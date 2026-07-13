using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GitFlick.ViewModels;

namespace GitFlick.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Palette navigation is handled on the tunnel (preview) so the window sees the keys
        // before whatever control has focus — the search box OR the list after a click.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        // Mouse users expect a double-click to open a repo / move a file across the staging line.
        RepoList.DoubleTapped += (_, _) => (DataContext as MainViewModel)?.OpenSelected();
        UnstagedList.DoubleTapped += (_, _) => Workspace?.StageCommand.Execute(Workspace.SelectedUnstagedFile);
        StagedList.DoubleTapped += (_, _) => Workspace?.UnstageCommand.Execute(Workspace.SelectedStagedFile);
    }

    private WorkspaceViewModel? Workspace => (DataContext as MainViewModel)?.Workspace;

    private void OnBackClick(object? sender, RoutedEventArgs e) => ReturnToPalette();

    /// <summary>Leaves the workspace and returns focus to the palette search box.</summary>
    private void ReturnToPalette()
    {
        if (DataContext is MainViewModel vm && vm.IsRepoOpen)
        {
            vm.CloseRepo();
            FocusInput();
        }
    }

    /// <summary>
    /// Puts the caret where the user expects it the instant the window is summoned.
    /// Call after the window is shown and activated.
    /// </summary>
    public void FocusInput()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        // Escape always steps back: workspace -> palette, palette -> hidden.
        if (e.Key == Key.Escape)
        {
            if (vm.IsRepoOpen)
            {
                ReturnToPalette();
            }
            else
            {
                Hide();
            }

            e.Handled = true;
            return;
        }

        // Everything below is palette-only; workspace keystrokes (commit box, etc.) pass through.
        if (!vm.IsPaletteVisible)
        {
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Key.O:
                    _ = PinRepositoryAsync();
                    e.Handled = true;
                    return;

                case Key.D:
                    vm.RemoveSelected();
                    e.Handled = true;
                    return;
            }
        }

        switch (e.Key)
        {
            case Key.Down:
                vm.MoveSelection(1);
                ScrollSelectionIntoView(vm);
                e.Handled = true;
                break;

            case Key.Up:
                vm.MoveSelection(-1);
                ScrollSelectionIntoView(vm);
                e.Handled = true;
                break;

            case Key.Enter:
                vm.OpenSelected();
                e.Handled = true;
                break;
        }
    }

    private async Task PinRepositoryAsync()
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        // The native folder dialog takes the foreground, which would otherwise trip
        // hide-on-deactivate and make the launcher vanish behind the picker.
        using var _ = (Application.Current as App)?.SuppressAutoHide();

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Pin a Git repository",
            AllowMultiple = false,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            vm.AddRepository(path);
        }

        FocusInput();
    }

    private void ScrollSelectionIntoView(MainViewModel vm)
    {
        if (vm.SelectedRepo is { } selected)
        {
            RepoList.ScrollIntoView(selected);
        }
    }
}
