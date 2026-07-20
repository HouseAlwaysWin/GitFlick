using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitFlick.Services;
using GitFlick.Services.Updates;
using GitFlick.ViewModels;

namespace GitFlick.Views;

/// <summary>Global settings (language + theme + updates), opened non-modally from the palette ⚙ button.</summary>
public partial class SettingsWindow : Window
{
    private static LocalizationService Loc => LocalizationService.Instance;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        // Installing restarts the app, so the view-model asks for confirmation through this owner window.
        if (DataContext is SettingsViewModel vm)
        {
            vm.ConfirmInstall = ConfirmInstallAsync;
        }
    }

    private async Task<bool> ConfirmInstallAsync(ReleaseInfo release)
    {
        var cancel = new Button
        {
            Content = Loc["Dialog_Cancel"],
            MinWidth = 92,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var install = new Button
        {
            Content = Loc["Update_InstallRestart"],
            MinWidth = 92,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        install.Classes.Add("primary");

        var dialog = new Window
        {
            Title = Loc["Update_ConfirmTitle"],
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border
            {
                Padding = new Thickness(22),
                Child = new StackPanel
                {
                    Spacing = 16,
                    MaxWidth = 380,
                    Children =
                    {
                        new TextBlock
                        {
                            TextWrapping = TextWrapping.Wrap,
                            Text = string.Format(Loc["Update_ConfirmBody"], release.TagName),
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Children = { cancel, install },
                        },
                    },
                },
            },
        };

        cancel.Click += (_, _) => dialog.Close(false);
        install.Click += (_, _) => dialog.Close(true);

        return await dialog.ShowDialog<bool>(this);
    }
}
