using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace GitFlick.Converters;

/// <summary>Green for a succeeded command, red for a failed one — used by the git command log.</summary>
public sealed class SuccessBrushConverter : IValueConverter
{
    public static readonly SuccessBrushConverter Instance = new();

    private static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#3FB950"));
    private static readonly IBrush Fail = new SolidColorBrush(Color.Parse("#F85149"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Ok : Fail;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
