using System;
using System.Collections.Generic;

namespace GitFlick.Models;

public enum GitRefKind
{
    LocalBranch,
    RemoteBranch,
    Tag,
}

/// <summary>A branch or tag pointing at a commit, as reported by <c>git log --decorate=full</c>.</summary>
public sealed record GitRef(string Name, GitRefKind Kind)
{
    // Exposed as bools so the badge template can switch style classes without a converter.
    public bool IsLocalBranch => Kind == GitRefKind.LocalBranch;

    public bool IsRemoteBranch => Kind == GitRefKind.RemoteBranch;

    public bool IsTag => Kind == GitRefKind.Tag;

    public override string ToString() => Name;
}

/// <summary>One row of the history: a commit plus what it takes to draw the graph.</summary>
public sealed record CommitInfo
{
    public required string Sha { get; init; }

    /// <summary>Parents in git's order — <c>Parents[0]</c> is the first parent.</summary>
    public required IReadOnlyList<string> Parents { get; init; }

    public required string Author { get; init; }

    public required DateTimeOffset When { get; init; }

    public required string Subject { get; init; }

    public IReadOnlyList<GitRef> Refs { get; init; } = [];

    /// <summary>True when HEAD points here.</summary>
    public bool IsHead { get; init; }

    public bool IsMerge => Parents.Count > 1;

    public string ShortSha => Sha.Length >= 7 ? Sha[..7] : Sha;

    public override string ToString() => Subject;
}
