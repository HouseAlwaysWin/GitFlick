using System.Collections.Generic;
using System.Linq;

namespace GitFlick.Models;

/// <summary>How a single side (index or worktree) of a file changed, from a porcelain XY code.</summary>
public enum GitFileState
{
    Unmodified,
    Modified,
    Added,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Unmerged,
    Untracked,
    Ignored,
}

/// <summary>Which porcelain v2 record produced an entry.</summary>
public enum GitChangeKind
{
    Ordinary,
    Renamed,
    Copied,
    Unmerged,
    Untracked,
    Ignored,
}

/// <summary>One changed path, with its staged (index) and unstaged (worktree) states.</summary>
public sealed record GitStatusEntry
{
    public required string Path { get; init; }

    /// <summary>The pre-rename/copy path, when <see cref="Kind"/> is Renamed or Copied.</summary>
    public string? OriginalPath { get; init; }

    public required GitChangeKind Kind { get; init; }

    public GitFileState StagedState { get; init; } = GitFileState.Unmodified;

    public GitFileState UnstagedState { get; init; } = GitFileState.Unmodified;

    /// <summary>Has changes recorded in the index (i.e. would be part of the next commit).</summary>
    public bool IsStaged =>
        Kind is not (GitChangeKind.Untracked or GitChangeKind.Ignored or GitChangeKind.Unmerged)
        && IsRealChange(StagedState);

    /// <summary>Has changes in the worktree that are not staged (includes untracked and conflicts).</summary>
    public bool IsUnstaged =>
        Kind is GitChangeKind.Untracked or GitChangeKind.Unmerged
        || (Kind is not GitChangeKind.Ignored && IsRealChange(UnstagedState));

    /// <summary>One-letter status for the worktree side (what the Unstaged list shows).</summary>
    public string UnstagedBadge => BadgeFor(unstaged: true);

    /// <summary>One-letter status for the index side (what the Staged list shows).</summary>
    public string StagedBadge => BadgeFor(unstaged: false);

    private string BadgeFor(bool unstaged)
    {
        if (Kind is GitChangeKind.Untracked)
        {
            return "?";
        }

        if (Kind is GitChangeKind.Unmerged)
        {
            return "U";
        }

        return Letter(unstaged ? UnstagedState : StagedState);
    }

    private static string Letter(GitFileState state) => state switch
    {
        GitFileState.Modified => "M",
        GitFileState.Added => "A",
        GitFileState.Deleted => "D",
        GitFileState.Renamed => "R",
        GitFileState.Copied => "C",
        GitFileState.TypeChanged => "T",
        GitFileState.Unmerged => "U",
        GitFileState.Untracked => "?",
        _ => "",
    };

    private static bool IsRealChange(GitFileState state) =>
        state is not (GitFileState.Unmodified or GitFileState.Untracked or GitFileState.Ignored);
}

/// <summary>A parsed <c>git status --porcelain=v2 --branch</c>.</summary>
public sealed record GitStatus
{
    /// <summary>Current branch, or null when HEAD is detached.</summary>
    public string? BranchName { get; init; }

    /// <summary>HEAD commit, or null on an unborn branch (a repo with no commits yet).</summary>
    public string? Oid { get; init; }

    public string? Upstream { get; init; }

    public int Ahead { get; init; }

    public int Behind { get; init; }

    public bool IsDetached => BranchName is null;

    public IReadOnlyList<GitStatusEntry> Entries { get; init; } = [];

    public IEnumerable<GitStatusEntry> Staged => Entries.Where(e => e.IsStaged);

    public IEnumerable<GitStatusEntry> Unstaged => Entries.Where(e => e.IsUnstaged);

    public bool IsClean => Entries.Count == 0;

    /// <summary>
    /// A cheap value that changes exactly when the displayed state would. The file watcher compares it
    /// before rebuilding anything, so the churn it can't help seeing — build output under bin/obj,
    /// git's own index rewrites — costs nothing and can't loop back into another refresh.
    /// </summary>
    public string Fingerprint => string.Join(
        '\n',
        new[] { BranchName, Oid, Upstream, Ahead.ToString(), Behind.ToString() }
            .Concat(Entries.Select(e => $"{e.Path}|{e.OriginalPath}|{e.Kind}|{e.StagedState}|{e.UnstagedState}")));
}
