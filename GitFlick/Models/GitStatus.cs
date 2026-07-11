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
}
