using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>
/// The Branch flyout's LOCAL / REMOTE split: remote-tracking branches show under their own section,
/// the two lists share one logical selection, checking out a remote branch DWIMs to a local tracking
/// branch, and Publish offers to push --set-upstream an unpublished local branch.
/// </summary>
public class BranchFlyoutRemoteTests
{
    private static WorkspaceViewModel ForFake(out FakeGitService git)
    {
        git = new FakeGitService();
        return new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));
    }

    [Fact]
    public async Task Remote_branches_are_listed_as_remote_and_reveal_the_section()
    {
        var vm = ForFake(out var git);
        git.StubRemoteBranches.Add("origin/main");
        git.StubRemoteBranches.Add("origin/feature");

        await vm.RefreshAsync();

        Assert.Equal(2, vm.RemoteBranches.Count);
        Assert.All(vm.RemoteBranches, b => Assert.True(b.IsRemote));
        Assert.Equal(2, vm.FilteredRemoteBranches.Count);
        Assert.True(vm.HasRemoteBranches);
    }

    [Fact]
    public async Task A_repo_with_no_remote_branches_hides_the_section()
    {
        var vm = ForFake(out _);   // StubRemoteBranches empty

        await vm.RefreshAsync();

        Assert.Empty(vm.RemoteBranches);
        Assert.False(vm.HasRemoteBranches);
    }

    [Fact]
    public async Task The_search_box_narrows_both_sections()
    {
        var vm = ForFake(out var git);
        git.StubRemoteBranches.Add("origin/main");
        git.StubRemoteBranches.Add("origin/feature");
        await vm.RefreshAsync();

        vm.BranchSearch = "feat";

        Assert.Single(vm.FilteredRemoteBranches);
        Assert.Equal("origin/feature", vm.FilteredRemoteBranches[0].Name);
    }

    [Fact]
    public async Task Checkout_of_a_remote_branch_creates_a_local_tracking_branch()
    {
        var vm = ForFake(out var git);
        git.StubRemoteBranches.Add("origin/feature");
        await vm.RefreshAsync();

        vm.SelectedRemoteBranch = vm.RemoteBranches.Single();
        await vm.CheckoutCommand.ExecuteAsync(null);

        // "origin/" is stripped, so git checks out (creating if needed) the local branch tracking it.
        Assert.Equal("feature", git.LastCheckout);
    }

    [Fact]
    public async Task Checkout_of_a_remote_branch_strips_only_the_remote_name()
    {
        var vm = ForFake(out var git);
        git.StubRemoteBranches.Add("origin/claude/wip");   // a branch name with its own slashes
        await vm.RefreshAsync();

        vm.SelectedRemoteBranch = vm.RemoteBranches.Single();
        await vm.CheckoutCommand.ExecuteAsync(null);

        Assert.Equal("claude/wip", git.LastCheckout);
    }

    [Fact]
    public void Picking_in_one_list_clears_the_selection_in_the_other()
    {
        var vm = ForFake(out _);
        var local = new GitBranch { Name = "main" };
        var remote = new GitBranch { Name = "origin/main", IsRemote = true };

        vm.SelectedLocalBranch = local;
        Assert.Same(local, vm.SelectedBranch);
        Assert.True(vm.SelectedIsLocal);

        vm.SelectedRemoteBranch = remote;
        Assert.Null(vm.SelectedLocalBranch);          // switching to a remote pick clears the local one
        Assert.Same(remote, vm.SelectedBranch);
        Assert.False(vm.SelectedIsLocal);

        vm.SelectedLocalBranch = local;               // and back again
        Assert.Null(vm.SelectedRemoteBranch);
        Assert.Same(local, vm.SelectedBranch);
    }

    [Fact]
    public void Publish_is_offered_only_for_an_unpublished_local_branch()
    {
        var vm = ForFake(out _);

        vm.SelectedBranch = new GitBranch { Name = "feature", Upstream = null };
        Assert.True(vm.CanPublishSelected);

        vm.SelectedBranch = new GitBranch { Name = "main", Upstream = "origin/main" };
        Assert.False(vm.CanPublishSelected);          // already has an upstream

        vm.SelectedRemoteBranch = new GitBranch { Name = "origin/main", IsRemote = true };
        Assert.False(vm.CanPublishSelected);          // a remote branch is nothing to publish
    }

    [Fact]
    public async Task Publish_pushes_the_selected_branch_with_set_upstream()
    {
        var vm = ForFake(out var git);
        git.StubRemotes.Add("origin");
        vm.SelectedBranch = new GitBranch { Name = "feature", Upstream = null };

        await vm.PublishBranchCommand.ExecuteAsync(null);

        Assert.Equal(("origin", "feature"), git.LastPublish);
    }

    [Fact]
    public async Task Publish_confirms_first_and_honours_a_decline()
    {
        var vm = ForFake(out var git);
        git.StubRemotes.Add("origin");
        vm.SelectedBranch = new GitBranch { Name = "feature", Upstream = null };
        vm.ConfirmPublishBranch = (_, _) => Task.FromResult(false);   // user says no

        await vm.PublishBranchCommand.ExecuteAsync(null);

        Assert.Null(git.LastPublish);
    }

    [Fact]
    public async Task Publish_does_nothing_for_a_branch_that_already_has_an_upstream()
    {
        var vm = ForFake(out var git);
        git.StubRemotes.Add("origin");
        vm.SelectedBranch = new GitBranch { Name = "main", Upstream = "origin/main" };

        await vm.PublishBranchCommand.ExecuteAsync(null);

        Assert.Null(git.LastPublish);
    }

    [Fact]
    public async Task Publish_reports_when_there_is_no_remote()
    {
        var vm = ForFake(out var git);   // StubRemotes empty
        vm.SelectedBranch = new GitBranch { Name = "feature", Upstream = null };

        await vm.PublishBranchCommand.ExecuteAsync(null);

        Assert.Null(git.LastPublish);
    }
}
