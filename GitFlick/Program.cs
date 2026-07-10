using Avalonia;
using System;
using System.Threading;

namespace GitFlick;

sealed class Program
{
    // Per-session, not machine-wide: a launcher belongs to one interactive desktop.
    private const string SingleInstanceMutexName = @"Local\GitFlick.SingleInstance";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // A second instance would race the first for the same hotkey and lose with
        // ERROR_HOTKEY_ALREADY_REGISTERED, reporting our own process as the culprit.
        using var singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);

        if (!isFirstInstance)
        {
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
