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
