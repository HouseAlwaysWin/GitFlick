using System;
using GitFlick.Models;
using GitFlick.Services.Interop;

namespace GitFlick.Services;

/// <summary>
/// The one place that knows how to put a global hotkey into effect: validate it, register it, persist
/// it, and refresh the tray tooltip that advertises it. Settings talks to this instead of the Win32
/// service, so it never has to know about registration ids or the tray.
/// <para>
/// A rejected combo must not leave the app with no hotkey at all — a tray-only app would become
/// unreachable — so a failed change re-registers whatever was working before.
/// </para>
/// </summary>
public sealed class HotkeyCoordinator
{
    private readonly IGlobalHotkeyService _hotkeys;
    private readonly ISettingsService _settings;
    private readonly Action<string>? _setTrayToolTip;

    public HotkeyCoordinator(
        IGlobalHotkeyService hotkeys, ISettingsService settings, Action<string>? setTrayToolTip = null)
    {
        _hotkeys = hotkeys;
        _settings = settings;
        _setTrayToolTip = setTrayToolTip;
    }

    /// <summary>The combo currently persisted (not necessarily one that registered successfully).</summary>
    public HotkeyDefinition Current => _settings.Current.Hotkey;

    /// <summary>
    /// Registers the stored combo at startup. Same path as a user change, minus the persist — so the
    /// tooltip is set in exactly one place.
    /// </summary>
    public bool TryRegisterCurrent(out string? error) => Register(Current, persist: false, out error);

    /// <summary>
    /// Validates, registers and persists <paramref name="hotkey"/>. On failure nothing is saved and the
    /// previous combo is put back, so the app keeps a working hotkey.
    /// </summary>
    public bool TryApply(HotkeyDefinition hotkey, out string? error)
    {
        if (!hotkey.HasRequiredModifier)
        {
            error = LocalizationService.Instance["Settings_Hotkey_NeedModifier"];
            return false;
        }

        if (!VirtualKeyMapper.TryToVirtualKey(hotkey.Key, out _))
        {
            error = string.Format(
                LocalizationService.Instance["Settings_Hotkey_UnusableKey"], hotkey.Key);
            return false;
        }

        var previous = Current;

        if (Register(hotkey, persist: true, out error))
        {
            return true;
        }

        // Put the working combo back; if even that fails there is nothing more to do about it.
        Register(previous, persist: false, out _);
        return false;
    }

    /// <summary>Restores the shipped Ctrl+Alt+G.</summary>
    public bool TryResetToDefault(out string? error) => TryApply(HotkeyDefinition.Default, out error);

    /// <summary>
    /// Drops the OS registration while Settings listens for a new combo. Without this, pressing the
    /// currently bound hotkey during capture would toggle the window instead of being recorded — the
    /// registration wins over keyboard focus.
    /// </summary>
    public void SuspendForCapture() => _hotkeys.Unregister();

    /// <summary>Re-arms the persisted combo after a capture that changed nothing.</summary>
    public void ResumeAfterCapture() => Register(Current, persist: false, out _);

    private bool Register(HotkeyDefinition hotkey, bool persist, out string? error)
    {
        if (!_hotkeys.TryRegister(hotkey, out error))
        {
            return false;
        }

        if (persist)
        {
            _settings.Current.Hotkey = hotkey;
            _settings.Save();
        }

        _setTrayToolTip?.Invoke($"GitFlick — {hotkey.ToDisplayString()}");
        error = null;
        return true;
    }
}
