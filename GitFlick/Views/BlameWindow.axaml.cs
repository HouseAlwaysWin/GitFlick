using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using GitFlick.Models;

namespace GitFlick.Views;

/// <summary>
/// A standalone per-line blame view for one file, bound to a dedicated <see cref="ViewModels.BlameViewModel"/>.
/// Right-clicking a line copies its full commit SHA.
/// </summary>
public partial class BlameWindow : Window
{
    public BlameWindow()
    {
        InitializeComponent();
    }

    private void OnCopyShaClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: BlameLine line } && !line.IsUncommitted)
        {
            _ = Clipboard?.SetTextAsync(line.Sha);
        }
    }
}
