using System.IO;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>The "pull first to sync" reminder and the quiet on-open remote check that feeds it.</summary>
public class SyncReminderTests
{
    private static WorkspaceViewModel ForFake(out FakeGitService git)
    {
        git = new FakeGitService();
        return new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));
    }

    [Fact]
    public void Behind_upstream_puts_the_count_on_the_pull_button()
    {
        var vm = ForFake(out _);

        vm.Behind = 3;

        Assert.True(vm.IsBehind);
        Assert.Contains("3", vm.PullLabel);        // "Pull ↓3"
        Assert.Contains("3", vm.PullTooltip);
    }

    [Fact]
    public void Level_with_upstream_drops_the_count_from_the_pull_button()
    {
        var vm = ForFake(out _);
        vm.Behind = 2;
        Assert.Contains("2", vm.PullLabel);

        vm.Behind = 0;

        Assert.False(vm.IsBehind);
        Assert.DoesNotContain("↓", vm.PullLabel);   // back to plain "Pull"
    }

    [Fact]
    public void Ahead_of_upstream_puts_the_count_on_the_push_button()
    {
        var vm = ForFake(out _);
        Assert.False(vm.IsAhead);
        Assert.DoesNotContain("↑", vm.PushLabel);   // plain "Push"

        vm.Ahead = 2;

        Assert.True(vm.IsAhead);
        Assert.Contains("2", vm.PushLabel);          // "Push ↑2"
        Assert.Contains("2", vm.PushTooltip);
    }

    [Fact]
    public async Task Remote_check_fetches_and_refreshes_the_counts()
    {
        var vm = ForFake(out var git);
        git.StubRemotes.Add("origin");
        git.StubStatus = new GitStatus { BranchName = "main", Upstream = "origin/main", Behind = 4, Ahead = 1 };

        await vm.CheckRemoteAsync();

        Assert.Equal(1, git.FetchCount);      // it actually fetched
        Assert.Equal(4, vm.Behind);
        Assert.Equal(1, vm.Ahead);
        Assert.True(vm.IsBehind);
        Assert.False(vm.IsCheckingRemote);    // flag is cleared afterwards
    }

    [Fact]
    public async Task Remote_check_is_skipped_when_there_is_no_remote()
    {
        var vm = ForFake(out var git);
        // StubRemotes left empty.

        await vm.CheckRemoteAsync();

        Assert.Equal(0, git.FetchCount);      // nothing to sync against — no fetch
    }
}
