using Avalonia.Controls;
using GitFlick.Services;

namespace GitFlick.Views;

/// <summary>
/// A side-by-side view of one file's diff: old lines on the left, new on the right, aligned and tinted.
/// Built from the unified diff already loaded in the workspace, so it needs no extra git call.
/// </summary>
public partial class SideBySideWindow : Window
{
    public SideBySideWindow()
    {
        InitializeComponent();
    }

    public SideBySideWindow(string filePath, string unifiedDiff)
        : this()
    {
        HeaderText.Text = filePath;
        RowsList.ItemsSource = SideBySideDiff.Build(unifiedDiff);
    }
}
