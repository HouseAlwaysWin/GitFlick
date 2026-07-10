using System;
using GitFlick.Models;

namespace GitFlick.Services;

/// <summary>
/// Keeps non-Windows builds running (and honest) until a platform implementation exists.
/// </summary>
public sealed class NoopGlobalHotkeyService : IGlobalHotkeyService
{
    public event EventHandler? HotkeyPressed
    {
        add { }
        remove { }
    }

    public bool TryRegister(HotkeyDefinition hotkey, out string? error)
    {
        error = "Global hotkeys are only implemented on Windows. Use the tray icon to open GitFlick.";
        return false;
    }

    public void Unregister()
    {
    }

    public void Dispose()
    {
    }
}
