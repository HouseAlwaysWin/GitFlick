using Avalonia;
using Avalonia.Media;

namespace GitFlick.Services;

/// <summary>
/// Applies the accent colour live by overwriting the App's accent <c>Color</c> resource keys —
/// every <c>DynamicResource</c> brush that references them (primary buttons, secondary chips, list
/// selection) repaints. Pure Color math, so it's AOT-safe. Mirrors GimmeCapture's
/// AvaloniaThemeResourceService.UpdateThemeColors.
/// </summary>
public static class AccentService
{
    public static Color Parse(string? hex) =>
        Color.TryParse(hex, out var c) ? c : Color.FromRgb(0x58, 0x8C, 0xF0);

    public static void Apply(Color accent)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        var r = app.Resources;
        r["AccentColor"] = accent;
        r["AccentColorHover"] = Lighten(accent, 0.15);
        r["AccentColorPressed"] = Darken(accent, 0.15);
        r["AccentColorDisabled"] = WithAlpha(accent, 0x55);
        r["AccentColorSubtle"] = WithAlpha(accent, 0x2E);
        r["AccentColorSubtleHover"] = WithAlpha(accent, 0x4D);
        r["AccentColorSubtlePressed"] = WithAlpha(accent, 0x66);
        r["AccentColorBorder"] = WithAlpha(accent, 0x73);

        // Best-effort: Fluent's own selection/focus accents. Fully applies to controls we don't
        // restyle only on the next launch, but the custom brushes above cover the load-bearing UI live.
        r["SystemAccentColor"] = accent;
        r["SystemAccentColorLight1"] = Lighten(accent, 0.20);
        r["SystemAccentColorLight2"] = Lighten(accent, 0.40);
        r["SystemAccentColorLight3"] = Lighten(accent, 0.60);
        r["SystemAccentColorDark1"] = Darken(accent, 0.20);
        r["SystemAccentColorDark2"] = Darken(accent, 0.40);
        r["SystemAccentColorDark3"] = Darken(accent, 0.60);
    }

    internal static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    internal static Color Lighten(Color c, double t) => Mix(c, Colors.White, t);

    internal static Color Darken(Color c, double t) => Mix(c, Colors.Black, t);

    private static Color Mix(Color c, Color other, double t) => Color.FromArgb(
        c.A,
        (byte)(c.R + (other.R - c.R) * t),
        (byte)(c.G + (other.G - c.G) * t),
        (byte)(c.B + (other.B - c.B) * t));
}
