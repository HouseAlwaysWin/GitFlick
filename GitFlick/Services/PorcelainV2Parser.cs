using System;
using System.Collections.Generic;
using GitFlick.Models;

namespace GitFlick.Services;

/// <summary>
/// Parses <c>git status --porcelain=v2 --branch</c>. v2 is the machine format: stable field
/// layout, and with core.quotepath=false the paths are literal UTF-8 (no octal escapes).
/// See git's status "Porcelain Format Version 2".
/// </summary>
internal static class PorcelainV2Parser
{
    public static GitStatus Parse(string output)
    {
        string? branchName = null;
        string? oid = null;
        string? upstream = null;
        var ahead = 0;
        var behind = 0;
        var entries = new List<GitStatusEntry>();

        foreach (var line in GitOutput.NonEmptyLines(output))
        {
            switch (line[0])
            {
                case '#':
                    ParseHeader(line, ref branchName, ref oid, ref upstream, ref ahead, ref behind);
                    break;
                case '1':
                    entries.Add(ParseOrdinary(line));
                    break;
                case '2':
                    entries.Add(ParseRenamed(line));
                    break;
                case 'u':
                    entries.Add(ParseUnmerged(line));
                    break;
                case '?':
                    entries.Add(ParseSimple(line, GitChangeKind.Untracked, GitFileState.Untracked));
                    break;
                case '!':
                    entries.Add(ParseSimple(line, GitChangeKind.Ignored, GitFileState.Ignored));
                    break;
            }
        }

        return new GitStatus
        {
            // git prints these literals when there is no real value.
            BranchName = branchName is null or "(detached)" ? null : branchName,
            Oid = oid is null or "(initial)" ? null : oid,
            Upstream = upstream,
            Ahead = ahead,
            Behind = behind,
            Entries = entries,
        };
    }

    private static void ParseHeader(
        string line, ref string? branchName, ref string? oid, ref string? upstream, ref int ahead, ref int behind)
    {
        // "# branch.head main", "# branch.ab +2 -1", ...
        var parts = line.Split(' ', 3);
        if (parts.Length < 2)
        {
            return;
        }

        var value = parts.Length > 2 ? parts[2] : string.Empty;

        switch (parts[1])
        {
            case "branch.oid":
                oid = value;
                break;
            case "branch.head":
                branchName = value;
                break;
            case "branch.upstream":
                upstream = value.Length == 0 ? null : value;
                break;
            case "branch.ab":
                foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token.StartsWith('+') && int.TryParse(token.AsSpan(1), out var a))
                    {
                        ahead = a;
                    }
                    else if (token.StartsWith('-') && int.TryParse(token.AsSpan(1), out var b))
                    {
                        behind = b;
                    }
                }

                break;
        }
    }

    // "1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path>"
    private static GitStatusEntry ParseOrdinary(string line)
    {
        var f = line.Split(' ', 9);
        var xy = f[1];
        return new GitStatusEntry
        {
            Path = f[8],
            Kind = GitChangeKind.Ordinary,
            StagedState = MapState(xy[0]),
            UnstagedState = MapState(xy[1]),
        };
    }

    // "2 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <Xscore> <path>\t<origPath>"
    private static GitStatusEntry ParseRenamed(string line)
    {
        var f = line.Split(' ', 10);
        var xy = f[1];
        var rest = f[9];
        var tab = rest.IndexOf('\t');
        var path = tab >= 0 ? rest[..tab] : rest;
        var original = tab >= 0 ? rest[(tab + 1)..] : null;
        var isCopy = xy[0] == 'C' || xy[1] == 'C';

        return new GitStatusEntry
        {
            Path = path,
            OriginalPath = original,
            Kind = isCopy ? GitChangeKind.Copied : GitChangeKind.Renamed,
            StagedState = MapState(xy[0]),
            UnstagedState = MapState(xy[1]),
        };
    }

    // "u <XY> <sub> <m1> <m2> <m3> <mW> <h1> <h2> <h3> <path>"
    private static GitStatusEntry ParseUnmerged(string line)
    {
        var f = line.Split(' ', 11);
        return new GitStatusEntry
        {
            Path = f[10],
            Kind = GitChangeKind.Unmerged,
            StagedState = GitFileState.Unmerged,
            UnstagedState = GitFileState.Unmerged,
        };
    }

    // "? <path>" or "! <path>"
    private static GitStatusEntry ParseSimple(string line, GitChangeKind kind, GitFileState state) => new()
    {
        Path = line[2..],
        Kind = kind,
        StagedState = state,
        UnstagedState = state,
    };

    private static GitFileState MapState(char code) => code switch
    {
        'M' => GitFileState.Modified,
        'A' => GitFileState.Added,
        'D' => GitFileState.Deleted,
        'R' => GitFileState.Renamed,
        'C' => GitFileState.Copied,
        'T' => GitFileState.TypeChanged,
        'U' => GitFileState.Unmerged,
        _ => GitFileState.Unmodified,
    };
}
