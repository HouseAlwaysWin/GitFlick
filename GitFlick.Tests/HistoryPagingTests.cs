using System;
using System.IO;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>
/// The History list loads a page at a time; "Load more" must grow the window until the whole repo is
/// reachable, and keep the selected commit across the reload. This is the "can't scroll past the cap" fix.
/// </summary>
public class HistoryPagingTests
{
    private static string Sha(int i) => i.ToString("x").PadLeft(40, '0');

    private static CommitInfo Commit(int i, int total) => new()
    {
        Sha = Sha(i),
        Parents = i < total - 1 ? new[] { Sha(i + 1) } : Array.Empty<string>(),
        Author = "Dev",
        When = DateTimeOffset.FromUnixTimeSeconds(2_000_000 - i),   // newest first, like git log
        Subject = "commit " + i,
    };

    private static WorkspaceViewModel Workspace(int commitCount, out FakeGitService git)
    {
        git = new FakeGitService();
        for (var i = 0; i < commitCount; i++)
        {
            git.StubCommits.Add(Commit(i, commitCount));
        }

        return new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));
    }

    [Fact]
    public async Task Load_more_grows_the_window_to_the_whole_repo_and_keeps_the_selection()
    {
        var vm = Workspace(350, out _);

        await vm.LoadHistoryAsync();
        Assert.Equal(300, vm.Commits.Count);   // only the first page
        Assert.True(vm.HasMoreCommits);

        vm.SelectedCommit = vm.Commits[42];
        var keptSha = vm.SelectedCommit!.Sha;

        await vm.LoadMoreCommitsCommand.ExecuteAsync(null);

        Assert.Equal(350, vm.Commits.Count);           // everything is now loaded
        Assert.False(vm.HasMoreCommits);               // nothing left to fetch
        Assert.Equal(keptSha, vm.SelectedCommit?.Sha); // selection survived the reload
    }

    [Fact]
    public async Task No_load_more_when_the_first_page_already_holds_the_whole_repo()
    {
        var vm = Workspace(12, out _);

        await vm.LoadHistoryAsync();

        Assert.Equal(12, vm.Commits.Count);
        Assert.False(vm.HasMoreCommits);
    }
}
