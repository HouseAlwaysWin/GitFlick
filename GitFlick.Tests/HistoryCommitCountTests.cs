using System;
using System.IO;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>The History header's "N commits" counter — it tracks the commits actually on screen.</summary>
public class HistoryCommitCountTests
{
    private static string Sha(int i) => i.ToString("x").PadLeft(40, '0');

    private static CommitInfo Commit(int i, string subject) => new()
    {
        Sha = Sha(i),
        Parents = Array.Empty<string>(),
        Author = "Dev",
        When = DateTimeOffset.FromUnixTimeSeconds(2_000_000 - i),
        Subject = subject,
    };

    private static async Task<WorkspaceViewModel> LoadedWorkspace()
    {
        var git = new FakeGitService();
        git.StubCommits.Add(Commit(0, "feat: add login"));
        git.StubCommits.Add(Commit(1, "fix: login redirect"));
        git.StubCommits.Add(Commit(2, "docs: update readme"));
        git.StubCommits.Add(Commit(3, "Fix CI pipeline"));

        var vm = new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));
        await vm.History.LoadHistoryAsync();
        return vm;
    }

    [Fact]
    public async Task Count_reflects_the_commits_on_screen()
    {
        var vm = await LoadedWorkspace();

        Assert.Equal(4, vm.History.CommitCount);
        Assert.True(vm.History.ShowCommitCount);
    }

    [Fact]
    public async Task Count_follows_a_client_side_filter()
    {
        var vm = await LoadedWorkspace();
        Assert.Equal(4, vm.History.CommitCount);

        vm.History.MessageFilter = "fix";            // narrows to the two fix commits, client-side

        Assert.Equal(2, vm.History.CommitCount);
    }

    [Fact]
    public void Empty_history_hides_the_count()
    {
        var vm = new WorkspaceViewModel(new FakeGitService(), new RepositoryItem("r", Path.GetTempPath()));

        Assert.Equal(0, vm.History.CommitCount);
        Assert.False(vm.History.ShowCommitCount);
    }

    [Fact]
    public void The_label_marks_unfetched_commits_with_a_plus()
    {
        var vm = new WorkspaceViewModel(new FakeGitService(), new RepositoryItem("r", Path.GetTempPath()));

        vm.History.CommitCount = 42;

        vm.History.HasMoreCommits = false;
        Assert.Contains("42", vm.History.CommitCountLabel);
        Assert.DoesNotContain("+", vm.History.CommitCountLabel);

        vm.History.HasMoreCommits = true;              // older commits still a "Load more" away
        Assert.Contains("42+", vm.History.CommitCountLabel);
    }
}
