using System;

namespace GitFlick.Services;

/// <summary>
/// Reads Git's conflict markers out of a working-tree file: the side labels Git wrote, whether any
/// markers are still present, and a rough binary sniff. The resolver labels its take-a-side buttons
/// from here (never "ours"/"theirs" — see docs/adr/0001) and warns before a file with leftover markers
/// is marked resolved.
/// </summary>
public static class ConflictMarkers
{
    public const string OursPrefix = "<<<<<<<";
    public const string TheirsPrefix = ">>>>>>>";
    private const string SeparatorPrefix = "=======";

    /// <summary>True if the text still contains any conflict marker — the footgun `git add` won't catch.</summary>
    public static bool HasMarkers(string text)
    {
        foreach (var line in EnumerateLines(text))
        {
            if (line.StartsWith(OursPrefix, StringComparison.Ordinal)
                || line.StartsWith(TheirsPrefix, StringComparison.Ordinal)
                || line == SeparatorPrefix
                || line.StartsWith(SeparatorPrefix, StringComparison.Ordinal) && line.Trim() == SeparatorPrefix)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The two side labels Git wrote (`&lt;&lt;&lt;&lt;&lt;&lt;&lt; HEAD` → "HEAD",
    /// `&gt;&gt;&gt;&gt;&gt;&gt;&gt; feature` → "feature"). Either is empty when its marker is absent.
    /// </summary>
    public static (string Ours, string Theirs) Labels(string text)
    {
        var ours = string.Empty;
        var theirs = string.Empty;

        foreach (var line in EnumerateLines(text))
        {
            if (ours.Length == 0 && line.StartsWith(OursPrefix, StringComparison.Ordinal))
            {
                ours = line[OursPrefix.Length..].Trim();
            }
            else if (line.StartsWith(TheirsPrefix, StringComparison.Ordinal))
            {
                theirs = line[TheirsPrefix.Length..].Trim();   // last one wins — the closing marker
            }
        }

        return (ours, theirs);
    }

    /// <summary>A NUL byte in the first chunk means Git couldn't 3-way merge it as text.</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        var limit = Math.Min(bytes.Length, 8000);
        for (var i = 0; i < limit; i++)
        {
            if (bytes[i] == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static System.Collections.Generic.IEnumerable<string> EnumerateLines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                yield return text[start..i].TrimEnd('\r');
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            yield return text[start..].TrimEnd('\r');
        }
    }
}
