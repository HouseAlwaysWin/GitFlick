using System;
using System.Collections.Generic;
using Avalonia.Input;

namespace GitFlick.Models;

/// <summary>
/// A global hotkey expressed in platform-neutral Avalonia terms. Win32 virtual-key codes
/// never appear above <see cref="Services.Interop.VirtualKeyMapper"/>.
/// </summary>
public sealed record HotkeyDefinition
{
    public KeyModifiers Modifiers { get; init; } = KeyModifiers.Control | KeyModifiers.Alt;

    public Key Key { get; init; } = Key.G;

    /// <summary>The combo GitFlick ships with: Ctrl+Alt+G.</summary>
    public static HotkeyDefinition Default { get; } = new();

    /// <summary>
    /// A global hotkey has to carry Ctrl, Alt or Win. Shift alone — or no modifier at all — would
    /// swallow an ordinary keystroke everywhere in Windows, so those are rejected.
    /// </summary>
    public bool HasRequiredModifier =>
        Modifiers.HasFlag(KeyModifiers.Control)
        || Modifiers.HasFlag(KeyModifiers.Alt)
        || Modifiers.HasFlag(KeyModifiers.Meta);

    public string ToDisplayString()
    {
        var parts = new List<string>(4);

        if (Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");

        parts.Add(Key.ToString());
        return string.Join('+', parts);
    }

    public override string ToString() => ToDisplayString();
}
