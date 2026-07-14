using System;
using System.Collections.Generic;
using System.Globalization;
using GitFlick.Models;

namespace GitFlick.Services;

/// <summary>
/// Parses the fixed <c>git log --format=...</c> record GitFlick asks for. Fields are separated
/// by NUL, which can never occur inside a commit subject, so no quoting games are needed.
/// </summary>
internal static class CommitLogParser
{
    /// <summary>%H sha, %P parents, %an author, %aI ISO date, %D decorations, %s subject.</summary>
    public const string Format = "%H%x00%P%x00%an%x00%aI%x00%D%x00%s";

    public static List<CommitInfo> Parse(string output)
    {
        var commits = new List<CommitInfo>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var f = line.Split('\0');
            if (f.Length < 6)
            {
                continue;
            }

            var parents = f[1].Length == 0
                ? []
                : f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var (refs, isHead) = ParseDecorations(f[4]);

            commits.Add(new CommitInfo
            {
                Sha = f[0],
                Parents = parents,
                Author = f[2],
                When = ParseDate(f[3]),
                Refs = refs,
                IsHead = isHead,
                Subject = f[5],
            });
        }

        return commits;
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)
            ? when
            : DateTimeOffset.MinValue;

    /// <summary>
    /// Turns "HEAD -&gt; refs/heads/main, refs/remotes/origin/main, tag: refs/tags/v1" into
    /// friendly names, and reports whether HEAD is here.
    /// </summary>
    private static (List<GitRef> Refs, bool IsHead) ParseDecorations(string decorations)
    {
        var refs = new List<GitRef>();
        var isHead = false;

        if (decorations.Length == 0)
        {
            return (refs, isHead);
        }

        foreach (var rawToken in decorations.Split(", ", StringSplitOptions.RemoveEmptyEntries))
        {
            var token = rawToken.Trim();

            // "HEAD -> refs/heads/main" — HEAD is attached to the branch that follows.
            if (token.StartsWith("HEAD -> ", StringComparison.Ordinal))
            {
                isHead = true;
                token = token["HEAD -> ".Length..];
            }
            else if (token == "HEAD")
            {
                // Detached HEAD: it decorates the commit but isn't a branch.
                isHead = true;
                continue;
            }

            // git prefixes annotated tags with "tag: " even under --decorate=full.
            if (token.StartsWith("tag: ", StringComparison.Ordinal))
            {
                token = token["tag: ".Length..];
            }

            if (TryStrip(token, "refs/heads/", out var local))
            {
                refs.Add(new GitRef(local, GitRefKind.LocalBranch));
            }
            else if (TryStrip(token, "refs/remotes/", out var remote))
            {
                // Skip "origin/HEAD" — it is a symbolic alias, not a branch you can check out.
                if (!remote.EndsWith("/HEAD", StringComparison.Ordinal))
                {
                    refs.Add(new GitRef(remote, GitRefKind.RemoteBranch));
                }
            }
            else if (TryStrip(token, "refs/tags/", out var tag))
            {
                refs.Add(new GitRef(tag, GitRefKind.Tag));
            }
        }

        return (refs, isHead);
    }

    private static bool TryStrip(string token, string prefix, out string name)
    {
        if (token.StartsWith(prefix, StringComparison.Ordinal))
        {
            name = token[prefix.Length..];
            return name.Length > 0;
        }

        name = string.Empty;
        return false;
    }
}
