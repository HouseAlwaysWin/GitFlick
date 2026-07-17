using System.Collections.Generic;

namespace GitFlick.Models;

/// <summary>
/// Curated accent presets for the settings swatch picker. Each is mid-toned: saturated enough to
/// carry white button text on the solid fills, light enough to stay visible on a light background.
/// Mirrors GimmeCapture's PresetColorPalette. The first entry is the app default.
/// </summary>
public static class PresetAccentPalette
{
    public static IReadOnlyList<string> Hexes { get; } =
    [
        "#588CF0", // blue (default)
        "#7C6CF0", // indigo
        "#A371F7", // purple
        "#E668B3", // pink
        "#E5534B", // red
        "#F0883E", // orange
        "#D29922", // amber
        "#3FB950", // green
        "#14B8A6", // teal
        "#2BB0C7", // cyan
    ];
}
