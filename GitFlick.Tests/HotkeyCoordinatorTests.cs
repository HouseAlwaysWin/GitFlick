using System.Text.Json;
using Avalonia.Input;
using GitFlick.Models;
using GitFlick.Services;

namespace GitFlick.Tests;

public class HotkeyCoordinatorTests
{
    private static HotkeyDefinition Combo(KeyModifiers modifiers, Key key) =>
        new() { Modifiers = modifiers, Key = key };

    [Fact]
    public void Applying_a_valid_combo_registers_and_persists_it()
    {
        var hotkeys = new FakeHotkeyService();
        var settings = new FakeSettingsService();
        var coordinator = new HotkeyCoordinator(hotkeys, settings);
        var combo = Combo(KeyModifiers.Control | KeyModifiers.Shift, Key.K);

        Assert.True(coordinator.TryApply(combo, out var error));

        Assert.Null(error);
        Assert.Equal(combo, settings.Current.Hotkey);
        Assert.Equal(combo, hotkeys.Registered[^1]);
        Assert.True(settings.SaveCount > 0);
    }

    [Theory]
    [InlineData(KeyModifiers.None)]
    [InlineData(KeyModifiers.Shift)]   // Shift alone would swallow the key system-wide
    public void A_combo_without_ctrl_alt_or_win_is_refused_before_the_os_is_touched(KeyModifiers modifiers)
    {
        var hotkeys = new FakeHotkeyService();
        var settings = new FakeSettingsService();
        var coordinator = new HotkeyCoordinator(hotkeys, settings);

        Assert.False(coordinator.TryApply(Combo(modifiers, Key.K), out var error));

        Assert.False(string.IsNullOrEmpty(error));
        Assert.Empty(hotkeys.Registered);
        Assert.Equal(0, settings.SaveCount);
        Assert.Equal(HotkeyDefinition.Default, settings.Current.Hotkey);
    }

    [Fact]
    public void A_key_with_no_virtual_key_mapping_is_refused()
    {
        var hotkeys = new FakeHotkeyService();
        var settings = new FakeSettingsService();
        var coordinator = new HotkeyCoordinator(hotkeys, settings);

        Assert.False(coordinator.TryApply(Combo(KeyModifiers.Control, Key.CapsLock), out var error));

        Assert.False(string.IsNullOrEmpty(error));
        Assert.Empty(hotkeys.Registered);
        Assert.Equal(0, settings.SaveCount);
    }

    [Fact]
    public void A_combo_the_os_refuses_leaves_the_previous_one_registered_and_saves_nothing()
    {
        var hotkeys = new FakeHotkeyService();
        var settings = new FakeSettingsService();
        var coordinator = new HotkeyCoordinator(hotkeys, settings);
        var taken = Combo(KeyModifiers.Control | KeyModifiers.Alt, Key.J);

        hotkeys.Reject = h => h == taken ? "already in use" : null;

        Assert.False(coordinator.TryApply(taken, out var error));

        Assert.Equal("already in use", error);
        Assert.Equal(HotkeyDefinition.Default, settings.Current.Hotkey);
        Assert.Equal(0, settings.SaveCount);

        // The working combo must be put back, or a tray-only app becomes unreachable.
        Assert.Equal(HotkeyDefinition.Default, hotkeys.Registered[^1]);
    }

    [Fact]
    public void Reset_restores_the_shipped_combo()
    {
        var hotkeys = new FakeHotkeyService();
        var settings = new FakeSettingsService();
        var coordinator = new HotkeyCoordinator(hotkeys, settings);

        Assert.True(coordinator.TryApply(Combo(KeyModifiers.Control | KeyModifiers.Alt, Key.M), out _));
        Assert.True(coordinator.TryResetToDefault(out _));

        Assert.Equal(HotkeyDefinition.Default, settings.Current.Hotkey);
        Assert.Equal(HotkeyDefinition.Default, hotkeys.Registered[^1]);
    }

    [Fact]
    public void Capture_drops_the_registration_and_resuming_puts_the_stored_combo_back()
    {
        var hotkeys = new FakeHotkeyService();
        var settings = new FakeSettingsService();
        var coordinator = new HotkeyCoordinator(hotkeys, settings);

        coordinator.SuspendForCapture();
        Assert.Equal(1, hotkeys.UnregisterCount);

        coordinator.ResumeAfterCapture();
        Assert.Equal(settings.Current.Hotkey, hotkeys.Registered[^1]);
    }

    [Fact]
    public void A_custom_hotkey_survives_the_settings_round_trip()
    {
        var settings = new AppSettings
        {
            Hotkey = Combo(KeyModifiers.Control | KeyModifiers.Shift, Key.F9),
        };

        var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
        var restored = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);

        Assert.NotNull(restored);
        Assert.Equal(settings.Hotkey, restored.Hotkey);
    }
}
