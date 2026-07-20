using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using GitFlick.Services;

namespace GitFlick.Converters;

/// <summary>Maps a side-by-side diff cell kind to its background tint (git-semantic, fixed across themes).</summary>
public sealed class DiffCellBrushConverter : IValueConverter
{
    private static readonly IBrush Removed = new SolidColorBrush(Color.FromArgb(0x33, 0xF8, 0x51, 0x49));
    private static readonly IBrush Added = new SolidColorBrush(Color.FromArgb(0x33, 0x3F, 0xB9, 0x50));
    private static readonly IBrush Empty = new SolidColorBrush(Color.FromArgb(0x0D, 0x80, 0x80, 0x80));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DiffCellKind.Removed => Removed,
        DiffCellKind.Added => Added,
        DiffCellKind.Empty => Empty,
        _ => Brushes.Transparent,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
