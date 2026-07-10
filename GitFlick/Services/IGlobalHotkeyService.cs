using System;
using GitFlick.Models;

namespace GitFlick.Services;

/// <summary>
/// Platform-neutral global hotkey registration. The Windows implementation is the only
/// thing that knows about RegisterHotKey; a future port swaps the implementation, not callers.
/// </summary>
public interface IGlobalHotkeyService : IDisposable
{
    /// <summary>Raised on the Avalonia UI thread when the registered combo is pressed.</summary>
    event EventHandler? HotkeyPressed;

    /// <summary>
    /// Registers <paramref name="hotkey"/>, replacing any previous registration.
    /// Returns false with a human-readable <paramref name="error"/> instead of throwing —
    /// a taken hotkey must not take the app down.
    /// </summary>
    bool TryRegister(HotkeyDefinition hotkey, out string? error);

    void Unregister();
}
