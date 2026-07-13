using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using GitFlick.ViewModels;
using GitFlick.Models;

namespace GitFlick.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Arrow keys and Enter belong to the list even while the caret is in the search box,
        // so they are handled here rather than letting the TextBox swallow them.
        SearchBox.KeyDown += OnSearchBoxKeyDown;

        // Double-click a file to move it across the staging line.
        UnstagedList.DoubleTapped += (_, _) => Workspace?.StageCommand.Execute(Workspace.SelectedUnstagedFile);
        StagedList.DoubleTapped += (_, _) => Workspace?.UnstageCommand.Execute(Workspace.SelectedStagedFile);
    }

    private WorkspaceViewModel? Workspace => (DataContext as MainViewModel)?.Workspace;

    /// <summary>
    /// Puts the caret where the user expects it the instant the window is summoned.
    /// Call after the window is shown and activated.
    /// </summary>
    public void FocusInput()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            if (e.Key == Key.Escape)
            {
                if (vm.IsRepoOpen)
                {
                    vm.CloseRepo();
                    FocusInput();
                }
                else
                {
                    Hide();
                }

                e.Handled = true;
                return;
            }

            if (e.KeyModifiers == KeyModifiers.Control && vm.IsPaletteVisible)
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
        }

        base.OnKeyDown(e);
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
