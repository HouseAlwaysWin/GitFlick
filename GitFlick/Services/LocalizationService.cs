using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using GitFlick.Models;

namespace GitFlick.Services;

/// <summary>
/// App-wide string table. A singleton so the <c>{loc:Tr Key}</c> markup extension can bind straight to
/// <see cref="Instance"/>; an <see cref="ObservableObject"/> so flipping <see cref="CurrentLanguage"/>
/// re-renders every bound string live (no restart). Lookup falls back language → English → the raw key,
/// so a missing translation degrades to English rather than a blank.
/// </summary>
public partial class LocalizationService : ObservableObject
{
    // Declared before Instance on purpose: static field initializers run in textual order, and the
    // Instance constructor iterates CultureFiles — so CultureFiles must already be assigned.
    private static readonly IReadOnlyDictionary<Language, string> CultureFiles = new Dictionary<Language, string>
    {
        [Language.English] = "en-US",
        [Language.TraditionalChinese] = "zh-TW",
        [Language.SimplifiedChinese] = "zh-CN",
        [Language.Japanese] = "ja-JP",
    };

    public static LocalizationService Instance { get; } = new();

    private readonly Dictionary<Language, Dictionary<string, string>> _tables = new();
    private Language _current = Language.English;

    private LocalizationService()
    {
        foreach (var (language, file) in CultureFiles)
        {
            _tables[language] = Load(file);
        }
    }

    /// <summary>The active language. Setting it refreshes every indexer binding in place.</summary>
    public Language CurrentLanguage
    {
        get => _current;
        set
        {
            if (_current == value)
            {
                return;
            }

            _current = value;

            // Every {loc:Tr} binding tracks this named property (see TrExtension) — one notification
            // re-runs their converters and re-pulls every translated string on screen.
            OnPropertyChanged(nameof(CurrentLanguage));
        }
    }

    /// <summary>Translated value for <paramref name="key"/>, falling back to English then the key itself.</summary>
    public string this[string key] => Resolve(_tables, _current, key);

    /// <summary>Pure lookup with the fallback chain (current → English → key). Testable without asset I/O.</summary>
    internal static string Resolve(
        IReadOnlyDictionary<Language, Dictionary<string, string>> tables, Language current, string key)
    {
        if (tables.TryGetValue(current, out var table) && table.TryGetValue(key, out var value))
        {
            return value;
        }

        if (current != Language.English
            && tables.TryGetValue(Language.English, out var english)
            && english.TryGetValue(key, out var fallback))
        {
            return fallback;
        }

        return key;
    }

    private static Dictionary<string, string> Load(string file)
    {
        // LogicalName set in the csproj: "GitFlick.Localization.<culture>.json".
        var resourceName = $"GitFlick.Localization.{file}.json";

        try
        {
            using var stream = typeof(LocalizationService).Assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return new Dictionary<string, string>();
            }

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            return JsonSerializer.Deserialize(json, LocalizationJsonContext.Default.DictionaryStringString)
                   ?? new Dictionary<string, string>();
        }
        catch (Exception)
        {
            // A missing/corrupt bundle must not take the app down — that language just falls back.
            return new Dictionary<string, string>();
        }
    }
}
