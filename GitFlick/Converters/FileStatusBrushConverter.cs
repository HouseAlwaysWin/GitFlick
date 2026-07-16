using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace GitFlick.Converters;

/// <summary>
/// Maps a git status letter (A/M/D/R/C/T/U/?) to a colour so file lists read at a glance:
/// green added, amber modified, red deleted, blue moved, orange conflict. Blank/unknown is grey.
/// The brushes are public so the on-screen legend renders from the very same values.
/// </summary>
public sealed class FileStatusBrushConverter : IValueConverter
{
    public static readonly FileStatusBrushConverter Instance = new();

    public static readonly IBrush AddedBrush = new SolidColorBrush(Color.Parse("#3FB950"));
    public static readonly IBrush ModifiedBrush = new SolidColorBrush(Color.Parse("#D29922"));
    public static readonly IBrush DeletedBrush = new SolidColorBrush(Color.Parse("#F85149"));
    public static readonly IBrush MovedBrush = new SolidColorBrush(Color.Parse("#588CF0"));
    public static readonly IBrush ConflictBrush = new SolidColorBrush(Color.Parse("#F0883E"));
    public static readonly IBrush NeutralBrush = new SolidColorBrush(Color.Parse("#9FB0C0"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var letter = (value as string ?? string.Empty).Trim();
        var key = letter.Length > 0 ? char.ToUpperInvariant(letter[0]) : ' ';
        return key switch
        {
            'A' => AddedBrush,
            'M' => ModifiedBrush,
            'D' => DeletedBrush,
            'R' => MovedBrush,
            'C' => MovedBrush,
            'T' => ModifiedBrush,
            'U' => ConflictBrush,
            _ => NeutralBrush,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
