using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using GitFlick.Services;

namespace GitFlick.Markup;

/// <summary>
/// <c>{loc:Tr Key}</c> → a live one-way binding that shows <c>LocalizationService.Instance["Key"]</c> and
/// re-renders when the language changes.
/// <para>
/// It binds to the <b>named</b> <see cref="LocalizationService.CurrentLanguage"/> property (which Avalonia
/// refreshes reliably on <see cref="System.ComponentModel.INotifyPropertyChanged"/>) and does the per-key
/// lookup in a converter. Binding straight to the <c>[Key]</c> indexer does <i>not</i> work — Avalonia's
/// reflection binding reads a string indexer once and never re-evaluates it on a property-changed signal.
/// Returning an <see cref="IBinding"/> also sidesteps compiled-binding datatype inference, so it compiles
/// under <c>AvaloniaUseCompiledBindingsByDefault</c>.
/// </para>
/// </summary>
public class TrExtension : MarkupExtension
{
    private static readonly IValueConverter Lookup = new KeyLookupConverter();

    public TrExtension()
    {
    }

    public TrExtension(string key)
    {
        Key = key;
    }

    public string Key { get; set; } = string.Empty;

    // The runtime Binding constructor is flagged RUC/RDC because reflection bindings can chase arbitrary
    // paths. Ours is the fixed, named CurrentLanguage property plus a static converter, and the app runs
    // JIT (IsAotCompatible turns the analyzers on but we don't publish AOT) — so neither warning is a real
    // hazard here.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Fixed named-property binding on LocalizationService; no AOT publish.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Reflection binding resolves a named property at runtime; the app is not AOT-compiled.")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(LocalizationService))]
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding
        {
            Source = LocalizationService.Instance,
            Path = nameof(LocalizationService.CurrentLanguage),
            Mode = BindingMode.OneWay,
            Converter = Lookup,
            ConverterParameter = Key,
        };

    /// <summary>Ignores the piped language value; looks the key (the parameter) up in the current language.</summary>
    private sealed class KeyLookupConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            parameter is string key ? LocalizationService.Instance[key] : string.Empty;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
