using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitFlick.Models;
using GitFlick.Services;
using GitFlick.Services.Updates;

namespace GitFlick.ViewModels;

/// <summary>
/// Backs the global Settings window (opened from the palette ⚙). Each appearance property is a bridge
/// over <see cref="ISettingsService"/>: the setter persists immediately and applies the live side-effect.
/// The Updates section drives the <see cref="UpdateService"/> (check · switch version · install).
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private static LocalizationService Loc => LocalizationService.Instance;

    private readonly ISettingsService _settings;
    private readonly UpdateService _updater;
    private readonly HotkeyCoordinator? _hotkeys;

    public SettingsViewModel(ISettingsService settings, UpdateService updater, HotkeyCoordinator? hotkeys = null)
    {
        _settings = settings;
        _updater = updater;
        _hotkeys = hotkeys;

        AccentSwatches = new ObservableCollection<AccentSwatch>();
        foreach (var hex in PresetAccentPalette.Hexes)
        {
            AccentSwatches.Add(new AccentSwatch(hex)
            {
                IsSelected = HexEquals(hex, _settings.Current.AccentColorHex),
            });
        }

        // Download progress/stage/errors are raised from the service (some off the UI thread), so marshal
        // every re-raise onto the UI thread before the bindings react.
        _updater.PropertyChanged += (_, e) => Dispatcher.UIThread.Post(() => OnUpdaterChanged(e.PropertyName));
    }

    /// <summary>Language combo index: 0 English · 1 繁體中文 · 2 简体中文 · 3 日本語.</summary>
    public int LanguageIndex
    {
        get => (int)_settings.Current.Language;
        set
        {
            _settings.Current.Language = (Language)value;
            _settings.Save();
            LocalizationService.Instance.CurrentLanguage = _settings.Current.Language;
            OnPropertyChanged();
        }
    }

    /// <summary>Theme combo index: 0 System · 1 Light · 2 Dark.</summary>
    public int ThemeVariantIndex
    {
        get => (int)_settings.Current.ThemeVariant;
        set
        {
            _settings.Current.ThemeVariant = (AppThemeVariant)value;
            _settings.Save();
            ThemeService.Apply(_settings.Current.ThemeVariant);
            OnPropertyChanged();
        }
    }

    /// <summary>Preset accent swatches; the one matching the persisted hex shows a selection ring.</summary>
    public ObservableCollection<AccentSwatch> AccentSwatches { get; }

    /// <summary>Persists the chosen accent, applies it live, and moves the selection ring.</summary>
    [RelayCommand]
    private void SelectAccent(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return;
        }

        _settings.Current.AccentColorHex = hex;
        _settings.Save();
        AccentService.Apply(AccentService.Parse(hex));

        foreach (var swatch in AccentSwatches)
        {
            swatch.IsSelected = HexEquals(swatch.Hex, hex);
        }
    }

    // ---- Global hotkey ------------------------------------------------------------------------

    /// <summary>The bound combo, e.g. <c>Ctrl+Alt+G</c>.</summary>
    public string HotkeyDisplay => (_hotkeys?.Current ?? _settings.Current.Hotkey).ToDisplayString();

    /// <summary>False when no coordinator was supplied (nothing to re-register), so the UI greys out.</summary>
    public bool CanEditHotkey => _hotkeys is not null;

    /// <summary>The capture button's label: the bound combo, or the prompt while it's listening.</summary>
    public string HotkeyButtonText => IsCapturingHotkey ? Loc["Settings_Hotkey_Press"] : HotkeyDisplay;

    /// <summary>True while the capture box is listening for the next combo.</summary>
    [ObservableProperty]
    private bool _isCapturingHotkey;

    partial void OnIsCapturingHotkeyChanged(bool value) => OnPropertyChanged(nameof(HotkeyButtonText));

    [ObservableProperty]
    private string? _hotkeyError;

    partial void OnHotkeyErrorChanged(string? value) => OnPropertyChanged(nameof(HasHotkeyError));

    public bool HasHotkeyError => !string.IsNullOrEmpty(HotkeyError);

    /// <summary>Arms the capture box; the window's code-behind feeds the next combo to <see cref="ApplyHotkey"/>.</summary>
    [RelayCommand]
    private void StartHotkeyCapture()
    {
        if (_hotkeys is null)
        {
            return;
        }

        HotkeyError = null;
        IsCapturingHotkey = true;

        // Let go of the OS registration, or pressing the bound combo would just toggle the window.
        _hotkeys.SuspendForCapture();
    }

    /// <summary>Esc, losing focus, or closing the window: keep the combo that was already bound.</summary>
    public void CancelHotkeyCapture()
    {
        if (!IsCapturingHotkey)
        {
            return;
        }

        IsCapturingHotkey = false;
        _hotkeys?.ResumeAfterCapture();
    }

    /// <summary>
    /// Applies a captured combo. On rejection the box stays armed (and suspended) with the reason
    /// shown, so the user can just press a different one.
    /// </summary>
    public bool ApplyHotkey(KeyModifiers modifiers, Key key)
    {
        if (_hotkeys is null)
        {
            return false;
        }

        if (!_hotkeys.TryApply(new HotkeyDefinition { Modifiers = modifiers, Key = key }, out var error))
        {
            HotkeyError = error;

            // TryApply put the old combo back; drop it again so capture keeps working.
            _hotkeys.SuspendForCapture();
            return false;
        }

        IsCapturingHotkey = false;
        HotkeyError = null;
        NotifyHotkeyChanged();
        return true;
    }

    /// <summary>Puts the shipped Ctrl+Alt+G back.</summary>
    [RelayCommand]
    private void ResetHotkey()
    {
        if (_hotkeys is null)
        {
            return;
        }

        IsCapturingHotkey = false;
        HotkeyError = _hotkeys.TryResetToDefault(out var error) ? null : error;
        NotifyHotkeyChanged();
    }

    private void NotifyHotkeyChanged()
    {
        OnPropertyChanged(nameof(HotkeyDisplay));
        OnPropertyChanged(nameof(HotkeyButtonText));
    }

    // ---- Updates ------------------------------------------------------------------------------

    /// <summary>Set by the window's code-behind: confirm (with a restart warning) before applying.</summary>
    public Func<ReleaseInfo, Task<bool>>? ConfirmInstall { get; set; }

    /// <summary>Releases available on GitHub, newest first. Populated by <see cref="CheckForUpdatesCommand"/>.</summary>
    public ObservableCollection<ReleaseInfo> AvailableReleases { get; } = [];

    /// <summary>The running version, e.g. <c>v0.1.0</c>.</summary>
    public string CurrentVersionText => "v" + _updater.CurrentVersion;

    [ObservableProperty]
    private bool _isLoadingReleases;

    [ObservableProperty]
    private string? _updateStatus;

    partial void OnUpdateStatusChanged(string? value) => OnPropertyChanged(nameof(HasUpdateStatus));

    public bool HasUpdateStatus => !string.IsNullOrEmpty(UpdateStatus);

    /// <summary>True once a check has populated the release catalog (drives the picker's visibility).</summary>
    public bool HasReleases => AvailableReleases.Count > 0;

    [ObservableProperty]
    private ReleaseInfo? _selectedRelease;

    partial void OnSelectedReleaseChanged(ReleaseInfo? value)
    {
        OnPropertyChanged(nameof(SelectedReleasePublished));
        OnPropertyChanged(nameof(CanInstallSelected));
        InstallSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Publish date of the picked release (local), or empty.</summary>
    public string SelectedReleasePublished =>
        SelectedRelease?.PublishedAt is { } at ? at.LocalDateTime.ToString("yyyy-MM-dd") : string.Empty;

    /// <summary>Install is offered for any release that isn't the one already running (up or down).</summary>
    public bool CanInstallSelected =>
        SelectedRelease is { } r
        && !_updater.IsSameAsCurrent(r)
        && r.GetPreferredZipAsset() != null
        && !_updater.IsDownloading;

    public bool IsDownloading => _updater.IsDownloading;

    public double DownloadProgress => _updater.DownloadProgress;

    public string DownloadPercentText => $"{_updater.DownloadProgress:0}%";

    public string? LastUpdateError => _updater.LastErrorMessage;

    public bool HasUpdateError => !string.IsNullOrEmpty(_updater.LastErrorMessage);

    public string DownloadStageText => _updater.DownloadStage switch
    {
        ArtifactDownloadStage.Downloading => Loc["Update_Stage_Downloading"],
        ArtifactDownloadStage.Verifying => Loc["Update_Stage_Verifying"],
        ArtifactDownloadStage.Completed => Loc["Update_Stage_Completed"],
        ArtifactDownloadStage.Cancelled => Loc["Update_Stage_Cancelled"],
        ArtifactDownloadStage.Failed => Loc["Update_Stage_Failed"],
        _ => string.Empty,
    };

    /// <summary>Silent update check on launch. Bridged to settings; persists immediately.</summary>
    public bool AutoCheckUpdates
    {
        get => _settings.Current.AutoCheckUpdates;
        set
        {
            _settings.Current.AutoCheckUpdates = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Fetches the release catalog, selects the running (or newest) version, and reports status.</summary>
    [RelayCommand]
    private async Task CheckForUpdates()
    {
        if (IsLoadingReleases)
        {
            return;
        }

        IsLoadingReleases = true;
        UpdateStatus = Loc["Update_Checking"];
        try
        {
            var releases = await _updater.GetAvailableReleasesAsync();
            AvailableReleases.Clear();
            foreach (var release in releases)
            {
                AvailableReleases.Add(release);
            }

            OnPropertyChanged(nameof(HasReleases));

            if (releases.Count == 0)
            {
                SelectedRelease = null;
                UpdateStatus = Loc["Update_NoReleases"];
                return;
            }

            SelectedRelease = releases.FirstOrDefault(_updater.IsSameAsCurrent) ?? releases[0];
            var newest = releases[0];
            UpdateStatus = _updater.IsNewerThanCurrent(newest)
                ? string.Format(Loc["Update_Available"], newest.TagName)
                : Loc["Update_UpToDate"];
        }
        catch (Exception ex)
        {
            UpdateStatus = string.Format(Loc["Update_CheckFailed"], ex.Message);
        }
        finally
        {
            IsLoadingReleases = false;
        }
    }

    /// <summary>Confirms, downloads (if needed), then applies the picked release — which restarts the app.</summary>
    [RelayCommand(CanExecute = nameof(CanInstallSelected))]
    private async Task InstallSelected()
    {
        if (SelectedRelease is not { } release)
        {
            return;
        }

        if (ConfirmInstall is { } confirm && !await confirm(release))
        {
            return;
        }

        UpdateStatus = string.Format(Loc["Update_Downloading"], release.TagName);
        var zip = _updater.IsUpdateDownloaded(release)
            ? _updater.DownloadedZipPath
            : await _updater.DownloadUpdateAsync(release);

        if (string.IsNullOrEmpty(zip))
        {
            UpdateStatus = Loc["Update_DownloadFailed"];
            return;
        }

        UpdateStatus = Loc["Update_Installing"];

        // Hands off to the detached swap script and exits the process; nothing after this runs.
        _updater.ApplyUpdate(zip, release.NormalizedVersion);
    }

    [RelayCommand]
    private void CancelDownload() => _updater.CancelDownload();

    private void OnUpdaterChanged(string? propertyName)
    {
        switch (propertyName)
        {
            case nameof(UpdateService.IsDownloading):
                OnPropertyChanged(nameof(IsDownloading));
                OnPropertyChanged(nameof(CanInstallSelected));
                InstallSelectedCommand.NotifyCanExecuteChanged();
                break;
            case nameof(UpdateService.DownloadProgress):
                OnPropertyChanged(nameof(DownloadProgress));
                OnPropertyChanged(nameof(DownloadPercentText));
                break;
            case nameof(UpdateService.DownloadStage):
                OnPropertyChanged(nameof(DownloadStageText));
                break;
            case nameof(UpdateService.LastErrorMessage):
                OnPropertyChanged(nameof(LastUpdateError));
                OnPropertyChanged(nameof(HasUpdateError));
                break;
        }
    }

    private static bool HexEquals(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One selectable accent chip in the settings picker. <see cref="IsSelected"/> drives the ring.</summary>
public partial class AccentSwatch : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public AccentSwatch(string hex)
    {
        Hex = hex;
        Brush = new SolidColorBrush(AccentService.Parse(hex));
    }

    public string Hex { get; }

    public IBrush Brush { get; }
}
