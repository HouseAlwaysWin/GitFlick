using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GitFlick.Models;
using GitFlick.Services;

namespace GitFlick.Tests;

public class LocalizationTests
{
    private static Dictionary<Language, Dictionary<string, string>> Tables(
        Dictionary<string, string>? en = null,
        Dictionary<string, string>? tw = null) => new()
    {
        [Language.English] = en ?? new Dictionary<string, string>(),
        [Language.TraditionalChinese] = tw ?? new Dictionary<string, string>(),
    };

    [Fact]
    public void Resolve_prefers_the_current_language()
    {
        var tables = Tables(
            en: new() { ["Greeting"] = "Hello" },
            tw: new() { ["Greeting"] = "你好" });

        Assert.Equal("你好", LocalizationService.Resolve(tables, Language.TraditionalChinese, "Greeting"));
    }

    [Fact]
    public void Resolve_falls_back_to_english_when_the_current_language_lacks_the_key()
    {
        var tables = Tables(
            en: new() { ["Greeting"] = "Hello" },
            tw: new());

        Assert.Equal("Hello", LocalizationService.Resolve(tables, Language.TraditionalChinese, "Greeting"));
    }

    [Fact]
    public void Resolve_returns_the_key_itself_when_it_is_missing_everywhere()
    {
        var tables = Tables(en: new(), tw: new());
        Assert.Equal("Absent_Key", LocalizationService.Resolve(tables, Language.TraditionalChinese, "Absent_Key"));
    }

    [Fact]
    public void Resolve_returns_the_key_for_english_when_english_lacks_it()
    {
        var tables = Tables(en: new(), tw: new());
        Assert.Equal("Absent_Key", LocalizationService.Resolve(tables, Language.English, "Absent_Key"));
    }

    [Theory]
    [InlineData("zh-TW.json")]
    [InlineData("zh-CN.json")]
    [InlineData("ja-JP.json")]
    public void Every_bundle_has_exactly_the_english_keys(string file)
    {
        var dir = LocalizationDir();
        var english = Keys(Path.Combine(dir, "en-US.json"));
        var other = Keys(Path.Combine(dir, file));

        var missing = english.Except(other).OrderBy(k => k).ToList();
        var extra = other.Except(english).OrderBy(k => k).ToList();

        Assert.True(missing.Count == 0, $"{file} is missing: {string.Join(", ", missing)}");
        Assert.True(extra.Count == 0, $"{file} has unexpected keys: {string.Join(", ", extra)}");
    }

    [Fact]
    public void English_bundle_is_substantial_and_has_no_blank_values()
    {
        var dir = LocalizationDir();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "en-US.json")));

        var count = 0;
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            Assert.False(string.IsNullOrWhiteSpace(prop.Value.GetString()), $"{prop.Name} is blank");
            count++;
        }

        Assert.True(count > 100, $"expected a full string table, found only {count} keys");
    }

    private static HashSet<string> Keys(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            keys.Add(prop.Name);
        }

        return keys;
    }

    private static string LocalizationDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "GitFlick", "Assets", "Localization");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate GitFlick/Assets/Localization from the test output.");
    }
}
