using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

        // Tunnelling, so a captured combo is recorded before the focused control reacts to it — a
        // Button would otherwise treat Space/Enter as "press me", and arrow keys as navigation.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
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

    /// <summary>Closing mid-capture must not leave the global hotkey unregistered.</summary>
    protected override void OnClosed(EventArgs e)
    {
        (DataContext as SettingsViewModel)?.CancelHotkeyCapture();
        base.OnClosed(e);
    }

    /// <summary>Records the next combo while the capture button is armed.</summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel { IsCapturingHotkey: true } vm)
        {
            return;
        }

        // Swallow everything while armed, so no keystroke leaks into the window behind the prompt.
        e.Handled = true;

        // A modifier on its own isn't a combo — keep waiting for the key it qualifies.
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            vm.CancelHotkeyCapture();
            return;
        }

        vm.ApplyHotkey(e.KeyModifiers, e.Key);
    }

    /// <summary>Clicking away from an armed capture box cancels it rather than leaving it listening.</summary>
    private void OnHotkeyCaptureLostFocus(object? sender, RoutedEventArgs e) =>
        (DataContext as SettingsViewModel)?.CancelHotkeyCapture();

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
