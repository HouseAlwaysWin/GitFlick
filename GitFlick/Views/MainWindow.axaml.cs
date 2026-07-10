using Avalonia.Controls;

namespace GitFlick.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Puts the caret where the user expects it the instant the window is summoned.
    /// Call after the window is shown and activated.
    /// </summary>
    public void FocusInput()
    {
        FocusProbe.Focus();
        FocusProbe.SelectAll();
    }
}
