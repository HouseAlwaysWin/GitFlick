using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace GitFlick.Converters;

/// <summary>
/// Maps a git status letter (A/M/D/R/C/T/U/?) to a colour so file lists read at a glance:
/// green added, amber modified, red deleted, blue moved, orange conflict; blank/unknown is grey.
/// Colours resolve from the App's <c>FileStatus*Brush</c> resources (single source of truth shared
/// with the footer legend); the hardcoded values are only fallbacks for design-time / tests where
/// no <see cref="Application"/> is running.
/// </summary>
public sealed class FileStatusBrushConverter : IValueConverter
{
    public static readonly FileStatusBrushConverter Instance = new();

    private static readonly IBrush AddedFallback = new SolidColorBrush(Color.Parse("#3FB950"));
    private static readonly IBrush ModifiedFallback = new SolidColorBrush(Color.Parse("#D29922"));
    private static readonly IBrush DeletedFallback = new SolidColorBrush(Color.Parse("#F85149"));
    private static readonly IBrush MovedFallback = new SolidColorBrush(Color.Parse("#588CF0"));
    private static readonly IBrush ConflictFallback = new SolidColorBrush(Color.Parse("#F0883E"));
    private static readonly IBrush NeutralFallback = new SolidColorBrush(Color.Parse("#9FB0C0"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var letter = (value as string ?? string.Empty).Trim();
        var key = letter.Length > 0 ? char.ToUpperInvariant(letter[0]) : ' ';
        return key switch
        {
            'A' => Resolve("FileStatusAddedBrush", AddedFallback),
            'M' => Resolve("FileStatusModifiedBrush", ModifiedFallback),
            'D' => Resolve("FileStatusDeletedBrush", DeletedFallback),
            'R' => Resolve("FileStatusMovedBrush", MovedFallback),
            'C' => Resolve("FileStatusMovedBrush", MovedFallback),
            'T' => Resolve("FileStatusModifiedBrush", ModifiedFallback),
            'U' => Resolve("FileStatusConflictBrush", ConflictFallback),
            _ => Resolve("FileStatusNeutralBrush", NeutralFallback),
        };
    }

    /// <summary>Looks a brush up in the running App's resources, falling back to a hardcoded value.</summary>
    internal static IBrush Resolve(string resourceKey, IBrush fallback)
    {
        if (Application.Current is { } app
            && app.TryGetResource(resourceKey, app.ActualThemeVariant, out var value)
            && value is IBrush brush)
        {
            return brush;
        }

        return fallback;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
