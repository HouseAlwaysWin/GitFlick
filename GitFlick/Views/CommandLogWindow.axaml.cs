using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using GitFlick.ViewModels;

namespace GitFlick.Views;

/// <summary>
/// A standalone, non-modal view of the git command log, so it doesn't crowd the main window.
/// Bound to the active <see cref="WorkspaceViewModel"/>; Refresh re-snapshots the shared log.
/// </summary>
public partial class CommandLogWindow : Window
{
    public CommandLogWindow()
    {
        InitializeComponent();
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
        => (DataContext as WorkspaceViewModel)?.RefreshCommandLog();

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel { CommandLog.Count: > 0 } workspace && Clipboard is { } clipboard)
        {
            var text = string.Join(
                Environment.NewLine,
                workspace.CommandLog.Select(entry =>
                    $"{entry.TimeDisplay}  {entry.Glyph}  {entry.Command}  ({entry.DurationDisplay})"));

            await clipboard.SetTextAsync(text);
        }
    }
}
