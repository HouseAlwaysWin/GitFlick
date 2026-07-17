using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace GitFlick.Converters;

/// <summary>Green for a succeeded command, red for a failed one — used by the git command log.
/// Resolves from the App's <c>CommandOkBrush</c>/<c>CommandFailBrush</c> resources (fallbacks below).</summary>
public sealed class SuccessBrushConverter : IValueConverter
{
    public static readonly SuccessBrushConverter Instance = new();

    private static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#3FB950"));
    private static readonly IBrush Fail = new SolidColorBrush(Color.Parse("#F85149"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true
            ? FileStatusBrushConverter.Resolve("CommandOkBrush", Ok)
            : FileStatusBrushConverter.Resolve("CommandFailBrush", Fail);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
