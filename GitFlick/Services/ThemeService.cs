using Avalonia;
using Avalonia.Styling;
using GitFlick.Models;

namespace GitFlick.Services;

/// <summary>
/// Maps the persisted <see cref="AppThemeVariant"/> onto Avalonia's
/// <see cref="Application.RequestedThemeVariant"/>. Setting it flips every control that resolves a
/// theme brush (Fluent's own, plus our <c>ThemeDictionaries</c> semantic keys) live — no restart.
/// <c>System</c> maps to <see cref="ThemeVariant.Default"/>, which follows the OS light/dark setting.
/// </summary>
public static class ThemeService
{
    public static void Apply(AppThemeVariant variant)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        app.RequestedThemeVariant = ToThemeVariant(variant);
    }

    /// <summary>Pure mapping (no <see cref="Application"/> dependency) so it's unit-testable.</summary>
    internal static ThemeVariant ToThemeVariant(AppThemeVariant variant) => variant switch
    {
        AppThemeVariant.Light => ThemeVariant.Light,
        AppThemeVariant.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };
}
