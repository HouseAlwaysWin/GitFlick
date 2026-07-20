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
using GitFlick.Services.Updates;
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
    private UpdateService? _updateService;
    private TrayIcon? _trayIcon;

    private bool _isExiting;

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

            // If a self-update was just applied, finalize it (verify the running exe + version match what
            // was promised, then clear the pending marker) before anything else comes up.
            UpdateService.VerifyPendingUpdateOnStartup(
                AppVersionInfo.CurrentVersion, RuntimePathProvider.GetExecutablePath());

            _settings = new SettingsService();
            _settings.Load();

            // Apply the persisted appearance before the window is built, so it renders correctly
            // with no first-frame flash.
            ThemeService.Apply(_settings.Current.ThemeVariant);
            AccentService.Apply(AccentService.Parse(_settings.Current.AccentColorHex));
            LocalizationService.Instance.CurrentLanguage = _settings.Current.Language;

            _gitService = new GitService(_settings.Current.GitExecutablePath);
            _updateService = new UpdateService(AppVersionInfo.CurrentVersion);
            _viewModel = new MainViewModel(_settings, _gitService) { UpdateService = _updateService };

            // Built eagerly so summoning it costs a Show(), not a XAML parse. Deliberately
            // NOT assigned to desktop.MainWindow -- that would auto-show it during startup.
            _mainWindow = new MainWindow { DataContext = _viewModel };
            _mainWindow.Closing += OnMainWindowClosing;

            desktop.ShutdownRequested += OnShutdownRequested;

            InitializeTrayIcon();
            InitializeHotkey();

            _ = CheckGitAvailabilityAsync();

            // It behaves like an ordinary windowed app: it opens on launch, minimises to the
            // taskbar, and only goes to the tray when you actually dismiss it (X, Esc, hotkey).
            // Losing focus does NOT hide it.
            ShowWindow();

            // Opt-in silent update check. Best-effort: it never blocks or disrupts startup.
            if (_settings.Current.AutoCheckUpdates)
            {
                _ = CheckForUpdatesOnStartupAsync();
            }
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

    /// <summary>
    /// Opt-in startup check: if GitHub has a newer release, show the palette banner nudging the user to
    /// ⚙ Settings → Updates. Silent on no-update / error — it must never get in the way of launching.
    /// </summary>
    private async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            var release = await _updateService!.CheckForUpdateAsync().ConfigureAwait(false);
            if (release is not null)
            {
                Dispatcher.UIThread.Post(() => _viewModel?.ReportUpdateAvailable(
                    string.Format(LocalizationService.Instance["Update_BannerAvailable"], release.TagName)));
            }
        }
        catch
        {
            // Best-effort only.
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

    /// <summary>
    /// Hotkey behaviour: bring it up, or dismiss it to the tray if it's already in front.
    /// A minimised window counts as "not in front", so the hotkey restores it.
    /// </summary>
    private void ToggleWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        if (_mainWindow.IsVisible
            && _mainWindow.IsActive
            && _mainWindow.WindowState != WindowState.Minimized)
        {
            HideWindow();
        }
        else
        {
            ShowWindow();
        }
    }

    /// <summary>Centres the window on whichever monitor the mouse is on.</summary>
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

        var wasHidden = !_mainWindow.IsVisible;

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        // Only re-centre and reset when coming back from the tray. Restoring from the taskbar
        // should leave the window where the user put it, and leave them where they were.
        if (wasHidden)
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
    }

    /// <summary>Dismisses to the tray. Only X, Esc and the hotkey do this — never focus loss.</summary>
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

        // X means "get out of my way", not "quit": it hides to the tray. Minimize is left alone
        // so it behaves like any other app, and only the tray's Exit really quits.
        e.Cancel = true;
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
