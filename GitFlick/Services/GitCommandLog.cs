using System;
using System.Collections.Generic;

namespace GitFlick.Services;

/// <summary>One git invocation as it was run: the command line, whether it succeeded, and how long it took.</summary>
public sealed record GitCommandLogEntry(string Command, bool Succeeded, long DurationMs, DateTime When)
{
    public string Glyph => Succeeded ? "✓" : "✗";

    public string TimeDisplay => When.ToString("HH:mm:ss");

    public string DurationDisplay => $"{DurationMs} ms";
}

/// <summary>
/// A bounded, thread-safe record of the git commands the app has run — every call funnels through
/// <see cref="GitService"/>, which appends here. Read a most-recent-first <see cref="Snapshot"/> for
/// display. Shared app-wide, so it spans every workspace.
/// </summary>
public sealed class GitCommandLog
{
    private const int MaxEntries = 300;

    private readonly object _gate = new();
    private readonly LinkedList<GitCommandLogEntry> _entries = new();

    public void Record(GitCommandLogEntry entry)
    {
        lock (_gate)
        {
            _entries.AddLast(entry);
            while (_entries.Count > MaxEntries)
            {
                _entries.RemoveFirst();   // drop the oldest
            }
        }
    }

    /// <summary>A most-recent-first copy, safe to hand to the UI while more commands run.</summary>
    public IReadOnlyList<GitCommandLogEntry> Snapshot()
    {
        lock (_gate)
        {
            var list = new List<GitCommandLogEntry>(_entries.Count);
            for (var node = _entries.Last; node is not null; node = node.Previous)
            {
                list.Add(node.Value);
            }

            return list;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }
}
