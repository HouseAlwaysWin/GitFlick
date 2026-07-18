using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using GitFlick.Models;

namespace GitFlick.Services;

/// <summary>
/// Parses <c>git blame --porcelain</c>. The porcelain format prints each commit's author/summary
/// block only the <em>first</em> time that commit appears; later hunks from the same commit carry
/// just the header line and the content. So we cache sha → meta and reuse it for repeat hunks.
/// </summary>
internal static partial class BlameParser
{
    // Hunk header: "<40-hex-sha> <orig-line> <final-line> [<lines-in-group>]".
    [GeneratedRegex(@"^([0-9a-fA-F]{40}) (\d+) (\d+)(?: (\d+))?$")]
    private static partial Regex HeaderPattern();

    private readonly record struct CommitMeta(string Author, DateTimeOffset When, string Summary);

    public static List<BlameLine> Parse(string output)
    {
        var lines = new List<BlameLine>();
        if (string.IsNullOrEmpty(output))
        {
            return lines;
        }

        var meta = new Dictionary<string, CommitMeta>();
        var rows = output.Replace("\r\n", "\n").Split('\n');

        var i = 0;
        while (i < rows.Length)
        {
            var header = HeaderPattern().Match(rows[i]);
            if (!header.Success)
            {
                i++;
                continue;
            }

            var sha = header.Groups[1].Value;
            var finalLine = int.Parse(header.Groups[3].Value, CultureInfo.InvariantCulture);
            i++;

            // Any "key value" meta lines that precede the TAB-prefixed content line.
            string? author = null, authorTime = null, authorTz = null, summary = null;
            while (i < rows.Length && !rows[i].StartsWith('\t'))
            {
                var kv = rows[i];
                i++;

                var space = kv.IndexOf(' ');
                var key = space < 0 ? kv : kv[..space];
                var value = space < 0 ? string.Empty : kv[(space + 1)..];

                switch (key)
                {
                    case "author": author = value; break;
                    case "author-time": authorTime = value; break;
                    case "author-tz": authorTz = value; break;
                    case "summary": summary = value; break;
                }
            }

            // The content itself is the TAB-prefixed line (may be empty).
            var content = string.Empty;
            if (i < rows.Length && rows[i].StartsWith('\t'))
            {
                content = rows[i][1..];
                i++;
            }

            // First sighting of a commit carries its meta; cache it. Repeat hunks reuse the cache.
            if (!meta.TryGetValue(sha, out var commit))
            {
                commit = new CommitMeta(author ?? string.Empty, ParseWhen(authorTime, authorTz), summary ?? string.Empty);
                meta[sha] = commit;
            }

            lines.Add(new BlameLine(sha, commit.Author, commit.When, commit.Summary, finalLine, content));
        }

        return lines;
    }

    /// <summary>Unix seconds + a "+0800"-style zone → a zoned <see cref="DateTimeOffset"/>.</summary>
    private static DateTimeOffset ParseWhen(string? unixSeconds, string? tz)
    {
        if (!long.TryParse(unixSeconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs))
        {
            return default;
        }

        var offset = TimeSpan.Zero;
        if (tz is { Length: 5 } && (tz[0] == '+' || tz[0] == '-')
            && int.TryParse(tz.AsSpan(1, 2), out var hours)
            && int.TryParse(tz.AsSpan(3, 2), out var minutes))
        {
            offset = new TimeSpan(hours, minutes, 0);
            if (tz[0] == '-')
            {
                offset = -offset;
            }
        }

        return DateTimeOffset.FromUnixTimeSeconds(secs).ToOffset(offset);
    }
}
