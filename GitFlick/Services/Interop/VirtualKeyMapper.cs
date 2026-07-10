using Avalonia.Input;

namespace GitFlick.Services.Interop;

/// <summary>
/// Avalonia exposes no public Key -> Win32 virtual-key mapping, so we keep our own.
/// Only covers keys that make sense as the trigger of a global hotkey.
/// </summary>
internal static class VirtualKeyMapper
{
    public static uint ToModifiers(KeyModifiers modifiers)
    {
        uint result = 0;

        if (modifiers.HasFlag(KeyModifiers.Alt)) result |= Win32.MOD_ALT;
        if (modifiers.HasFlag(KeyModifiers.Control)) result |= Win32.MOD_CONTROL;
        if (modifiers.HasFlag(KeyModifiers.Shift)) result |= Win32.MOD_SHIFT;
        if (modifiers.HasFlag(KeyModifiers.Meta)) result |= Win32.MOD_WIN;

        return result;
    }

    public static bool TryToVirtualKey(Key key, out uint virtualKey)
    {
        // These ranges are contiguous in Avalonia's Key enum, so arithmetic beats a 60-case switch.
        if (key is >= Key.A and <= Key.Z)
        {
            virtualKey = (uint)(0x41 + (key - Key.A));
            return true;
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            virtualKey = (uint)(0x30 + (key - Key.D0));
            return true;
        }

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            virtualKey = (uint)(0x60 + (key - Key.NumPad0));
            return true;
        }

        if (key is >= Key.F1 and <= Key.F24)
        {
            virtualKey = (uint)(0x70 + (key - Key.F1));
            return true;
        }

        virtualKey = key switch
        {
            Key.Back => 0x08,
            Key.Tab => 0x09,
            Key.Return => 0x0D,
            Key.Escape => 0x1B,
            Key.Space => 0x20,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.End => 0x23,
            Key.Home => 0x24,
            Key.Left => 0x25,
            Key.Up => 0x26,
            Key.Right => 0x27,
            Key.Down => 0x28,
            Key.Insert => 0x2D,
            Key.Delete => 0x2E,
            Key.OemSemicolon => 0xBA,
            Key.OemPlus => 0xBB,
            Key.OemComma => 0xBC,
            Key.OemMinus => 0xBD,
            Key.OemPeriod => 0xBE,
            Key.OemQuestion => 0xBF,
            Key.OemTilde => 0xC0,
            Key.OemOpenBrackets => 0xDB,
            Key.OemPipe => 0xDC,
            Key.OemCloseBrackets => 0xDD,
            Key.OemQuotes => 0xDE,
            _ => 0,
        };

        return virtualKey != 0;
    }
}
