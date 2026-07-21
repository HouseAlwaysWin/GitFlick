using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>
/// The "Pull from…" branch completions: remote-tracking refs narrowed to one remote, with the
/// "&lt;remote&gt;/" prefix stripped so they're what <c>git pull &lt;remote&gt; &lt;branch&gt;</c> expects.
/// </summary>
public class PullSourceBranchTests
{
    private static readonly string[] RemoteRefs =
    [
        "origin/main",
        "origin/claude/md-docs",     // a branch name that itself contains slashes
        "upstream/main",
        "upstream/release",
        "origin2/main",              // a remote whose name merely starts with another's
    ];

    [Fact]
    public void Keeps_only_the_chosen_remote_and_strips_its_prefix()
    {
        var branches = WorkspaceViewModel.BranchesOnRemote(RemoteRefs, "origin");

        Assert.Equal(["main", "claude/md-docs"], branches);
    }

    [Fact]
    public void A_remote_that_prefixes_another_is_not_confused_with_it()
    {
        // "origin2/main" must not leak into "origin", nor vice versa: each keeps only its own.
        Assert.Equal(["main"], WorkspaceViewModel.BranchesOnRemote(RemoteRefs, "origin2"));
        Assert.Equal(2, WorkspaceViewModel.BranchesOnRemote(RemoteRefs, "origin").Count);
    }

    [Fact]
    public void Another_remote_gets_its_own_branches()
    {
        Assert.Equal(["main", "release"], WorkspaceViewModel.BranchesOnRemote(RemoteRefs, "upstream"));
    }

    [Fact]
    public void An_unknown_remote_offers_nothing()
    {
        Assert.Empty(WorkspaceViewModel.BranchesOnRemote(RemoteRefs, "nope"));
    }
}
