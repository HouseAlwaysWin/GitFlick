using System;
using System.Collections.Generic;
using System.Globalization;

namespace GitFlick.Services;

/// <summary>Which side a cell in the side-by-side diff represents (drives its background tint).</summary>
public enum DiffCellKind
{
    /// <summary>No line here — filler opposite an unpaired add/remove.</summary>
    Empty,

    /// <summary>Unchanged line, shown on both sides.</summary>
    Context,

    /// <summary>A line only in the old file (left side).</summary>
    Removed,

    /// <summary>A line only in the new file (right side).</summary>
    Added,
}

/// <summary>One aligned row of a side-by-side diff: an old-side (left) cell and a new-side (right) cell.</summary>
public sealed record DiffRow(
    int? LeftLine, string LeftText, DiffCellKind LeftKind,
    int? RightLine, string RightText, DiffCellKind RightKind);

/// <summary>
/// Turns a unified diff into aligned left/right rows for the compare window. Removed and added lines
/// inside a hunk are paired up (line N old ↔ line N new); leftovers get an empty cell on the other side.
/// </summary>
public static class SideBySideDiff
{
    public static IReadOnlyList<DiffRow> Build(string unifiedDiff)
    {
        var rows = new List<DiffRow>();
        var removed = new List<(int Line, string Text)>();
        var added = new List<(int Line, string Text)>();
        var oldLine = 0;
        var newLine = 0;

        void Flush()
        {
            var max = Math.Max(removed.Count, added.Count);
            for (var i = 0; i < max; i++)
            {
                var hasLeft = i < removed.Count;
                var hasRight = i < added.Count;
                rows.Add(new DiffRow(
                    hasLeft ? removed[i].Line : null,
                    hasLeft ? removed[i].Text : string.Empty,
                    hasLeft ? DiffCellKind.Removed : DiffCellKind.Empty,
                    hasRight ? added[i].Line : null,
                    hasRight ? added[i].Text : string.Empty,
                    hasRight ? DiffCellKind.Added : DiffCellKind.Empty));
            }

            removed.Clear();
            added.Clear();
        }

        foreach (var line in GitOutput.NonEmptyLines(unifiedDiff ?? string.Empty))
        {
            // Real diff lines always carry a prefix char (' ', '+', '-'); skip a "\ No newline at end
            // of file" marker (NonEmptyLines already drops blank and split-artifact lines).
            if (line.StartsWith("\\", StringComparison.Ordinal))
            {
                continue;
            }

            var kind = DiffLineClassifier.Classify(line);

            if (kind == DiffLineKind.Header)
            {
                continue;
            }

            if (kind == DiffLineKind.Hunk)
            {
                Flush();
                (oldLine, newLine) = ParseHunk(line, oldLine, newLine);
                continue;
            }

            var content = line.Length > 0 ? line[1..] : string.Empty;

            switch (kind)
            {
                case DiffLineKind.Context:
                    Flush();
                    rows.Add(new DiffRow(oldLine, content, DiffCellKind.Context, newLine, content, DiffCellKind.Context));
                    oldLine++;
                    newLine++;
                    break;

                case DiffLineKind.Removed:
                    removed.Add((oldLine, content));
                    oldLine++;
                    break;

                case DiffLineKind.Added:
                    added.Add((newLine, content));
                    newLine++;
                    break;
            }
        }

        Flush();
        return rows;
    }

    /// <summary>Reads the start lines out of "@@ -oldStart,n +newStart,m @@".</summary>
    private static (int Old, int New) ParseHunk(string line, int oldFallback, int newFallback)
    {
        var oldStart = oldFallback;
        var newStart = newFallback;

        foreach (var token in line.Split(' '))
        {
            if (token.Length > 1 && token[0] == '-' && TryStart(token[1..], out var o))
            {
                oldStart = o;
            }
            else if (token.Length > 1 && token[0] == '+' && TryStart(token[1..], out var n))
            {
                newStart = n;
            }
        }

        return (oldStart, newStart);

        static bool TryStart(string token, out int value) =>
            int.TryParse(token.Split(',')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
