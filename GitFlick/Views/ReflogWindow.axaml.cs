using Avalonia.Controls;
using Avalonia.Interactivity;
using GitFlick.ViewModels;

namespace GitFlick.Views;

/// <summary>
/// A standalone, non-modal reflog view. Bound to the active <see cref="WorkspaceViewModel"/>;
/// each entry offers "Reset to here" to recover from a bad reset/rebase/checkout.
/// </summary>
public partial class ReflogWindow : Window
{
    public ReflogWindow()
    {
        InitializeComponent();
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
        => _ = (DataContext as WorkspaceViewModel)?.LoadReflogAsync();
}
