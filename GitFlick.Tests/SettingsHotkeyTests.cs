using Avalonia.Input;
using GitFlick.Models;
using GitFlick.Services;
using GitFlick.Services.Updates;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>
/// The capture flow the Settings window drives: arm the box, then feed it the combo the user pressed.
/// The window's KeyDown handler does nothing more than call these.
/// </summary>
public class SettingsHotkeyTests
{
    private static (SettingsViewModel Vm, FakeHotkeyService Hotkeys, FakeSettingsService Settings) Build()
    {
        var hotkeys = new FakeHotkeyService();
        var settings = new FakeSettingsService();
        var vm = new SettingsViewModel(
            settings, new UpdateService("0.1.0"), new HotkeyCoordinator(hotkeys, settings));

        return (vm, hotkeys, settings);
    }

    [Fact]
    public void Arming_the_box_prompts_and_releases_the_registration()
    {
        var (vm, hotkeys, _) = Build();

        vm.StartHotkeyCaptureCommand.Execute(null);

        Assert.True(vm.IsCapturingHotkey);

        // Otherwise pressing the bound combo would toggle the window instead of being recorded.
        Assert.Equal(1, hotkeys.UnregisterCount);

        // The button shows the prompt, not the combo.
        Assert.NotEqual(vm.HotkeyDisplay, vm.HotkeyButtonText);
    }

    [Fact]
    public void Cancelling_puts_the_previous_registration_back()
    {
        var (vm, hotkeys, settings) = Build();

        vm.StartHotkeyCaptureCommand.Execute(null);
        vm.CancelHotkeyCapture();

        Assert.False(vm.IsCapturingHotkey);
        Assert.Equal(settings.Current.Hotkey, hotkeys.Registered[^1]);
        Assert.Equal(vm.HotkeyDisplay, vm.HotkeyButtonText);
    }

    [Fact]
    public void Capturing_a_combo_binds_it_and_shows_it()
    {
        var (vm, _, settings) = Build();

        vm.StartHotkeyCaptureCommand.Execute(null);

        Assert.True(vm.ApplyHotkey(KeyModifiers.Control | KeyModifiers.Alt, Key.J));

        Assert.False(vm.IsCapturingHotkey);
        Assert.False(vm.HasHotkeyError);
        Assert.Equal("Ctrl+Alt+J", vm.HotkeyDisplay);
        Assert.Equal(Key.J, settings.Current.Hotkey.Key);
    }

    [Fact]
    public void A_combo_another_app_owns_keeps_the_box_armed_and_says_why()
    {
        var (vm, hotkeys, settings) = Build();
        var wanted = new HotkeyDefinition { Modifiers = KeyModifiers.Control | KeyModifiers.Alt, Key = Key.J };

        hotkeys.Reject = h => h == wanted ? "already in use" : null;
        vm.StartHotkeyCaptureCommand.Execute(null);

        Assert.False(vm.ApplyHotkey(wanted.Modifiers, wanted.Key));

        Assert.True(vm.IsCapturingHotkey);            // still listening, so the user can try another
        Assert.Equal("already in use", vm.HotkeyError);
        Assert.True(vm.HasHotkeyError);
        Assert.Equal(HotkeyDefinition.Default, settings.Current.Hotkey);

        // Re-suspended after the failed attempt restored the old combo.
        Assert.Equal(2, hotkeys.UnregisterCount);
    }

    [Fact]
    public void A_combo_with_no_real_modifier_keeps_the_box_armed()
    {
        var (vm, _, settings) = Build();

        vm.StartHotkeyCaptureCommand.Execute(null);

        Assert.False(vm.ApplyHotkey(KeyModifiers.Shift, Key.J));

        Assert.True(vm.IsCapturingHotkey);
        Assert.True(vm.HasHotkeyError);
        Assert.Equal(HotkeyDefinition.Default, settings.Current.Hotkey);
    }

    [Fact]
    public void Reset_puts_ctrl_alt_g_back()
    {
        var (vm, _, settings) = Build();

        Assert.True(vm.ApplyHotkey(KeyModifiers.Control | KeyModifiers.Alt, Key.M));
        vm.ResetHotkeyCommand.Execute(null);

        Assert.Equal(HotkeyDefinition.Default, settings.Current.Hotkey);
        Assert.Equal("Ctrl+Alt+G", vm.HotkeyDisplay);
    }
}
