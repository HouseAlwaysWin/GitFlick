using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace GitFlick.Services;

/// <summary>
/// A small, forgiving glob matcher for the History search's path boxes — comma-separated patterns in
/// the style of VS Code's "files to include/exclude" ("*.md, docs/, src/**.cs").
///
/// git does the real filtering (this is a pathspec it never sees), so this only has to agree with the
/// user's intuition for the suggestion list: a pattern matches when it matches the whole path OR just
/// the file name, which is what makes a bare "*.md" drop "docs/notes.md" the way people expect.
/// </summary>
public static class PathGlob
{
    /// <summary>Splits a comma/semicolon-separated pattern list, dropping blanks.</summary>
    public static IReadOnlyList<string> Split(string patterns)
    {
        var list = new List<string>();
        foreach (var raw in patterns.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var pattern = raw.Trim();
            if (pattern.Length > 0)
            {
                list.Add(pattern);
            }
        }

        return list;
    }

    /// <summary>True when <paramref name="path"/> matches any of the comma-separated patterns.</summary>
    public static bool MatchesAny(string path, string patterns)
    {
        foreach (var pattern in Split(patterns))
        {
            if (Matches(path, pattern))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when <paramref name="path"/> matches one glob pattern.</summary>
    public static bool Matches(string path, string pattern)
    {
        var subject = path.Replace('\\', '/').Trim();
        var glob = pattern.Replace('\\', '/').Trim();
        if (glob.Length == 0 || subject.Length == 0)
        {
            return false;
        }

        // "docs/" (or "docs") reads as "everything under docs/", the way a folder pattern should.
        var folder = glob.TrimEnd('/');
        if (folder.Length > 0 && !folder.Contains('*') && !folder.Contains('?')
            && (subject.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)
                || subject.Equals(folder, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var regex = new Regex(ToPattern(glob), RegexOptions.IgnoreCase);
        if (regex.IsMatch(subject))
        {
            return true;
        }

        // A bare "*.md" should catch "docs/notes.md" too, so try the leaf when the pattern has no slash.
        if (!glob.Contains('/'))
        {
            var slash = subject.LastIndexOf('/');
            var leaf = slash >= 0 ? subject[(slash + 1)..] : subject;
            return regex.IsMatch(leaf);
        }

        return false;
    }

    // Glob -> anchored regex. "**" spans separators, "*" stops at one, "?" is a single character.
    private static string ToPattern(string glob)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        sb.Append(".*");
                        i++;
                    }
                    else
                    {
                        sb.Append("[^/]*");
                    }

                    break;

                case '?':
                    sb.Append("[^/]");
                    break;

                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        return sb.Append('$').ToString();
    }
}
