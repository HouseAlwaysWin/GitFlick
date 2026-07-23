using Avalonia.Controls;
using Avalonia.Interactivity;
using GitFlick.Models;
using GitFlick.ViewModels;

namespace GitFlick.Views;

/// <summary>
/// Add and remove the repository's remotes, bound to the active <see cref="WorkspaceViewModel"/>.
/// Non-modal, like the command-log and reflog windows.
/// </summary>
public partial class RemotesWindow : Window
{
    public RemotesWindow()
    {
        InitializeComponent();
    }

    /// <summary>Per-row Remove reads its own remote from the button's DataContext, as elsewhere in the app.</summary>
    private void OnRemoveRemoteClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is GitRemote remote
            && DataContext is WorkspaceViewModel workspace)
        {
            workspace.RemoveRemoteCommand.Execute(remote);
        }
    }
}
