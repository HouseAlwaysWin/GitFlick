using System;
using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitFlick.Models;
using GitFlick.Services;

namespace GitFlick.ViewModels;

/// <summary>
/// Backs the global Settings window (opened from the palette ⚙). Each property is a bridge over
/// <see cref="ISettingsService"/>: the setter persists immediately and applies the live side-effect.
/// Mirrors the bridge shape used by <see cref="WorkspaceViewModel.CommitTemplate"/>.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;

    public SettingsViewModel(ISettingsService settings)
    {
        _settings = settings;

        AccentSwatches = new ObservableCollection<AccentSwatch>();
        foreach (var hex in PresetAccentPalette.Hexes)
        {
            AccentSwatches.Add(new AccentSwatch(hex)
            {
                IsSelected = HexEquals(hex, _settings.Current.AccentColorHex),
            });
        }
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
