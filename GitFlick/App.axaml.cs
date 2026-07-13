using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using GitFlick.Services;
using GitFlick.Services.Interop;
using GitFlick.ViewModels;
using GitFlick.Views;

namespace GitFlick;

public partial class App : Application
{
    private MainWindow? _mainWindow;
    private MainViewModel? _viewModel;
    private ISettingsService? _settings;
    private IGlobalHotkeyService? _hotkeys;
    private IGitService? _gitService;
    private TrayIcon? _trayIcon;

    private bool _isExiting;

    /// <summary>
    /// Guards against the window hiding itself during the activation handover, when the
    /// outgoing foreground window can briefly bounce a Deactivated event off ours.
    /// Module ② will reuse this seam for the folder picker.
    /// </summary>
    private bool _suppressDeactivateHide;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Tray-first: the process must outlive every window. Without this the lifetime
            // shuts down as soon as the last window closes.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _settings = new SettingsService();
            _settings.Load();

            _gitService = new GitService(_settings.Current.GitExecutablePath);
            _viewModel = new MainViewModel(_settings, _gitService);

            // Built eagerly so summoning it costs a Show(), not a XAML parse. Deliberately
            // NOT assigned to desktop.MainWindow -- that would auto-show it during startup.
            _mainWindow = new MainWindow { DataContext = _viewModel };
            _mainWindow.Closing += OnMainWindowClosing;
            _mainWindow.Deactivated += OnMainWindowDeactivated;

            WarmUpWindow();

            desktop.ShutdownRequested += OnShutdownRequested;

            InitializeTrayIcon();
            InitializeHotkey();

            _ = CheckGitAvailabilityAsync();

            // Show the window on launch so starting the app isn't invisible. It still lives in
            // the tray afterwards: close/Esc/deactivate hides it, the hotkey re-summons it.
            ShowWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Warns in the window if git can't be found (spec §1). Fire-and-forget: a slow PATH probe
    /// must never delay the tray coming up.
    /// </summary>
    private async Task CheckGitAvailabilityAsync()
    {
        var version = await _gitService!.GetVersionAsync().ConfigureAwait(false);

        if (version is null)
        {
            Dispatcher.UIThread.Post(() => _viewModel?.ReportGitMissing(
                "Git was not found. Install Git and add it to PATH, or set \"GitExecutablePath\" in settings.json."));
        }
    }

    private void InitializeTrayIcon()
    {
        var showItem = new NativeMenuItem("Show");
        showItem.Click += OnTrayShowClick;

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += OnTrayExitClick;

        var menu = new NativeMenu { showItem, new NativeMenuItemSeparator(), exitItem };

        using var stream = AssetLoader.Open(new Uri("avares://GitFlick/Assets/gitflick.ico"));

        // Order matters, and not for an obvious reason. On Avalonia 12.1.0, assigning Icon to
        // an already-visible TrayIcon leaves it absent from the notification area entirely --
        // reproduced with Avalonia's own stock .ico, so it is not this file. Constructing it
        // hidden, assigning the icon, then flipping IsVisible makes the registration stick.
        _trayIcon = new TrayIcon
        {
            IsVisible = false,
            Icon = new WindowIcon(stream),
            ToolTipText = "GitFlick",
            Menu = menu,
        };

        _trayIcon.Clicked += OnTrayClicked;

        TrayIcon.SetIcons(this, [_trayIcon]);

        _trayIcon.IsVisible = true;
    }

    private void InitializeHotkey()
    {
        _hotkeys = OperatingSystem.IsWindows()
            ? new Win32GlobalHotkeyService()
            : new NoopGlobalHotkeyService();

        _hotkeys.HotkeyPressed += OnHotkeyPressed;

        var hotkey = _settings!.Current.Hotkey;

        if (_hotkeys.TryRegister(hotkey, out var error))
        {
            SetTrayToolTip($"GitFlick — {hotkey.ToDisplayString()}");
            return;
        }

        // A dead hotkey makes a tray-only app invisible and unusable. Say so, loudly, and
        // still come up: the tray menu remains a working way in.
        _viewModel!.ReportHotkeyFailure(error ?? "The global hotkey could not be registered.");
        SetTrayToolTip("GitFlick — hotkey inactive");
        ShowWindow();
    }

    private void OnHotkeyPressed(object? sender, EventArgs e) => ToggleWindow();

    private void ToggleWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        if (_mainWindow.IsVisible && _mainWindow.IsActive)
        {
            HideWindow();
        }
        else
        {
            ShowWindow();
        }
    }

    /// <summary>
    /// Creates the native window once, off-screen and unactivated, so the first hotkey press
    /// pays a ~2 ms Show() instead of ~300 ms of window creation. Measured, not assumed.
    /// </summary>
    private void WarmUpWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _suppressDeactivateHide = true;
        _mainWindow.ShowActivated = false;
        _mainWindow.Position = new PixelPoint(-32000, -32000);
        _mainWindow.Show();
        _mainWindow.Hide();
        _mainWindow.ShowActivated = true;
        _suppressDeactivateHide = false;
    }

    /// <summary>Centres the launcher on whichever monitor the mouse is on.</summary>
    private void PositionWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        var screens = _mainWindow.Screens;
        Screen? target = null;

        if (OperatingSystem.IsWindows() && Win32.GetCursorPos(out var cursor))
        {
            target = screens.ScreenFromPoint(new PixelPoint(cursor.X, cursor.Y));
        }

        target ??= screens.Primary ?? (screens.All.Count > 0 ? screens.All[0] : null);

        if (target is null)
        {
            return;
        }

        var area = target.WorkingArea;
        var width = (int)(_mainWindow.Width * target.Scaling);
        var height = (int)(_mainWindow.Height * target.Scaling);

        _mainWindow.Position = new PixelPoint(
            area.X + ((area.Width - width) / 2),
            area.Y + ((area.Height - height) / 2));
    }

    private void ShowWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _suppressDeactivateHide = true;

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        if (!_mainWindow.IsVisible)
        {
            PositionWindow();
            _viewModel?.ResetForSummon();
        }

        _mainWindow.Show();
        _mainWindow.Activate();

        if (OperatingSystem.IsWindows() && _mainWindow.TryGetPlatformHandle() is { } handle)
        {
            ForceForeground(handle.Handle);
        }

        _mainWindow.FocusInput();

        // Clear once the activation messages have drained, not before.
        Dispatcher.UIThread.Post(() => _suppressDeactivateHide = false, DispatcherPriority.Background);
    }

    /// <summary>
    /// Holds off hide-on-deactivate while a modal or native dialog owns the foreground.
    /// Dispose to re-arm it. This is the seam module ②'s folder picker needed.
    /// </summary>
    public IDisposable SuppressAutoHide()
    {
        _suppressDeactivateHide = true;
        return new AutoHideScope(this);
    }

    private sealed class AutoHideScope(App app) : IDisposable
    {
        public void Dispose() => Dispatcher.UIThread.Post(
            () => app._suppressDeactivateHide = false,
            DispatcherPriority.Background);
    }

    private void HideWindow() => _mainWindow?.Hide();

    /// <summary>
    /// Avalonia's Activate() alone does not reliably foreground a window on Windows, because
    /// the OS refuses SetForegroundWindow from a background process. Arriving here from
    /// WM_HOTKEY is what makes it legal: the process that received the hotkey holds the
    /// foreground grant. The AttachThreadInput path only matters if that grant has lapsed.
    /// </summary>
    private static void ForceForeground(IntPtr hwnd)
    {
        if (Win32.IsIconic(hwnd))
        {
            Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
        }

        if (Win32.SetForegroundWindow(hwnd))
        {
            return;
        }

        var foregroundWindow = Win32.GetForegroundWindow();
        var foregroundThread = Win32.GetWindowThreadProcessId(foregroundWindow, IntPtr.Zero);
        var currentThread = Win32.GetCurrentThreadId();

        if (foregroundThread == 0 || foregroundThread == currentThread)
        {
            return;
        }

        if (!Win32.AttachThreadInput(currentThread, foregroundThread, true))
        {
            return;
        }

        try
        {
            Win32.BringWindowToTop(hwnd);
            Win32.SetForegroundWindow(hwnd);
        }
        finally
        {
            Win32.AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        // The title bar's X means "get out of my way", not "quit". Only the tray exits.
        e.Cancel = true;
        HideWindow();
    }

    private void OnMainWindowDeactivated(object? sender, EventArgs e)
    {
        if (_isExiting || _suppressDeactivateHide)
        {
            return;
        }

        HideWindow();
    }

    private void SetTrayToolTip(string text)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.ToolTipText = text;
        }
    }

    private void OnTrayClicked(object? sender, EventArgs e) => Dispatcher.UIThread.Post(ShowWindow);

    private void OnTrayShowClick(object? sender, EventArgs e) => Dispatcher.UIThread.Post(ShowWindow);

    private void OnTrayExitClick(object? sender, EventArgs e) => Shutdown();

    private void Shutdown()
    {
        _isExiting = true;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    /// <summary>Unregisters the hotkey and joins the message loop before the process goes away.</summary>
    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        _isExiting = true;
        _hotkeys?.Dispose();
        _hotkeys = null;
    }
}
