using System;
using System.Collections.Generic;
using System.Globalization;
using GitFlick.Models;

namespace GitFlick.Services;

/// <summary>
/// Parses the fixed <c>git reflog --format=...</c> record: NUL-separated selector, sha, subject, ISO date.
/// </summary>
internal static class ReflogParser
{
    /// <summary>%gd selector, %H sha, %gs reflog subject, %cI committer ISO date.</summary>
    public const string Format = "%gd%x00%H%x00%gs%x00%cI";

    public static List<ReflogEntry> Parse(string output)
    {
        var entries = new List<ReflogEntry>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var f = line.Split('\0');
            if (f.Length < 4)
            {
                continue;
            }

            var when = DateTimeOffset.TryParse(f[3], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;

            entries.Add(new ReflogEntry(f[0], f[1], f[2], when));
        }

        return entries;
    }
}
