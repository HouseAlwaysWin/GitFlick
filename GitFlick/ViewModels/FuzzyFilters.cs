using Avalonia.Controls;
using GitFlick.Services;

namespace GitFlick.ViewModels;

/// <summary>
/// Filters for <see cref="AutoCompleteBox"/> pickers. Exposed as statics so XAML can bind one with
/// <c>{x:Static}</c>, and so every branch picker narrows the same way the palette does — a
/// subsequence match, not a prefix ("cmd" finds "claude/md-docs").
/// </summary>
public static class FuzzyFilters
{
    /// <summary>Fuzzy match over the item's text; an empty query keeps everything.</summary>
    public static AutoCompleteFilterPredicate<object?> Any { get; } =
        (search, item) =>
            string.IsNullOrEmpty(search)
            || (item is string text && FuzzyMatcher.TryMatch(text, search, out _));
}
