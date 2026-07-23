using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>
/// The background auto-sync: it fetches, and refreshes the view <i>only</i> when the remote actually
/// moved — so the periodic poll doesn't yank the view out from under you every few minutes for nothing.
/// </summary>
public class AutoSyncTests
{
    private static (WorkspaceViewModel Vm, FakeGitService Git) ForRepoWithRemote()
    {
        var git = new FakeGitService();
        git.StubRemotes.Add("origin");
        var vm = new WorkspaceViewModel(git, new RepositoryItem("r", @"C:\repo"));
        return (vm, git);
    }

    [Fact]
    public async Task When_the_remote_moved_it_refreshes_the_view()
    {
        var (vm, git) = ForRepoWithRemote();
        await vm.RefreshAsync();                       // seed: Behind 0
        var statusBefore = git.StatusCallCount;

        git.StubStatus = new GitStatus { BranchName = "main", Behind = 3 };   // remote gained commits
        await vm.AutoSyncAsync();

        Assert.Equal(3, vm.Behind);
        // CheckRemote's status read + a follow-up RefreshAsync read = 2 more.
        Assert.True(git.StatusCallCount - statusBefore >= 2,
            "a moved remote should trigger a full refresh");
    }

    [Fact]
    public async Task When_nothing_changed_it_does_not_refresh_the_view()
    {
        var (vm, git) = ForRepoWithRemote();
        await vm.RefreshAsync();                       // Behind stays 0
        var statusBefore = git.StatusCallCount;

        await vm.AutoSyncAsync();

        // Only CheckRemote's single status read — no disruptive reload.
        Assert.Equal(1, git.StatusCallCount - statusBefore);
    }

    [Fact]
    public async Task A_repo_with_no_remote_fetches_nothing()
    {
        var git = new FakeGitService();   // StubRemotes empty
        var vm = new WorkspaceViewModel(git, new RepositoryItem("r", @"C:\repo"));

        await vm.AutoSyncAsync();

        Assert.Equal(0, git.FetchCount);
    }

    [Fact]
    public async Task It_stands_down_while_a_command_is_running()
    {
        var (vm, git) = ForRepoWithRemote();
        vm.IsBusy = true;

        await vm.AutoSyncAsync();

        Assert.Equal(0, git.FetchCount);   // must not interfere with an in-flight operation
    }
}
