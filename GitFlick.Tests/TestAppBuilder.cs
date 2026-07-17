using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Xunit;

// LocalizationService.Instance is a shared singleton; the live-switch test mutates CurrentLanguage.
// Serialize the suite so no other test reads it mid-flip.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace GitFlick.Tests;

/// <summary>Entry point for the headless Avalonia session. Includes Fluent so a Window can be shown.</summary>
public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}
