using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitFlick.Services;
using GitFlick.ViewModels;

namespace GitFlick.Views;

/// <summary>
/// Resolve a conflicted operation (merge / cherry-pick / revert / rebase). Non-modal, like the app's
/// other tool windows; it derives from the same conflicted-operation state the toolbar banner does.
/// AvaloniaEdit's document isn't a bind target, so the view model pushes/pulls the editor text through
/// delegates — the same pattern the main diff editor uses.
/// </summary>
public partial class ConflictWindow : Window
{
    private static LocalizationService Loc => LocalizationService.Instance;
    private ConflictResolverViewModel? _vm;

    public ConflictWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.Finished -= OnFinished;
        }

        _vm = DataContext as ConflictResolverViewModel;
        if (_vm is null)
        {
            return;
        }

        _vm.SetEditorText = text => ConflictEditor.Text = text;
        _vm.GetEditorText = () => ConflictEditor.Text;
        _vm.ConfirmAbort = ConfirmAbortAsync;
        _vm.Finished += OnFinished;
    }

    private void OnFinished(object? sender, EventArgs e) => Close();

    /// <summary>Abort throws away any resolved work, so make the user opt in.</summary>
    private async Task<bool> ConfirmAbortAsync()
    {
        var cancel = new Button
        {
            Content = Loc["Dialog_Cancel"],
            MinWidth = 92,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var proceed = new Button
        {
            Content = Loc["Conflict_Abort"],
            MinWidth = 92,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        proceed.Classes.Add("danger");

        var dialog = new Window
        {
            Title = Loc["Conflict_Abort"],
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = Loc["Conflict_AbortConfirm"],
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 360,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, proceed },
                    },
                },
            },
        };

        cancel.Click += (_, _) => dialog.Close(false);
        proceed.Click += (_, _) => dialog.Close(true);

        return await dialog.ShowDialog<bool>(this);
    }
}
