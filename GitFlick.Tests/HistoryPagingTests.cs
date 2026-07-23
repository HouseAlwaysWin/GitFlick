using System;
using System.IO;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.Services;
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

        await vm.History.LoadHistoryAsync();
        Assert.Equal(300, vm.History.Commits.Count);   // only the first page
        Assert.True(vm.History.HasMoreCommits);

        vm.History.SelectedCommit = vm.History.Commits[42];
        var keptSha = vm.History.SelectedCommit!.Sha;

        await vm.History.LoadMoreCommitsCommand.ExecuteAsync(null);

        Assert.Equal(350, vm.History.Commits.Count);           // everything is now loaded
        Assert.False(vm.History.HasMoreCommits);               // nothing left to fetch
        Assert.Equal(keptSha, vm.History.SelectedCommit?.Sha); // selection survived the reload
    }

    [Fact]
    public async Task No_load_more_when_the_first_page_already_holds_the_whole_repo()
    {
        var vm = Workspace(12, out _);

        await vm.History.LoadHistoryAsync();

        Assert.Equal(12, vm.History.Commits.Count);
        Assert.False(vm.History.HasMoreCommits);
    }

    [Fact]
    public async Task Commit_operations_invoke_the_expected_git_commands()
    {
        var vm = Workspace(1, out var git);
        await vm.History.LoadHistoryAsync();
        var sha = Sha(0);

        vm.PromptTagName = () => Task.FromResult<string?>("v1");
        vm.PromptBranchName = () => Task.FromResult<string?>("feature");
        vm.PromptResetMode = _ => Task.FromResult<GitResetMode?>(GitResetMode.Hard);
        vm.ConfirmRebase = _ => Task.FromResult(true);

        async Task Run(Func<Task> op)
        {
            vm.History.SelectedCommit = vm.History.Commits[0];
            await op();
        }

        await Run(() => vm.AddTagCommand.ExecuteAsync(null));
        await Run(() => vm.CreateBranchHereCommand.ExecuteAsync(null));
        await Run(() => vm.RevertCommitCommand.ExecuteAsync(null));
        await Run(() => vm.RebaseOntoCommand.ExecuteAsync(null));
        await Run(() => vm.ResetToCommitCommand.ExecuteAsync(null));

        Assert.Contains($"tag v1 {sha}", git.Operations);
        Assert.Contains($"branch feature {sha}", git.Operations);
        Assert.Contains($"revert {sha}", git.Operations);
        Assert.Contains($"rebase {sha}", git.Operations);
        Assert.Contains($"reset Hard {sha}", git.Operations);
    }

    [Fact]
    public async Task Cancelling_a_prompt_runs_no_git_command()
    {
        var vm = Workspace(1, out var git);
        await vm.History.LoadHistoryAsync();
        vm.History.SelectedCommit = vm.History.Commits[0];

        vm.PromptTagName = () => Task.FromResult<string?>(null);        // cancelled
        vm.PromptResetMode = _ => Task.FromResult<GitResetMode?>(null); // cancelled
        vm.ConfirmRebase = _ => Task.FromResult(false);                 // declined

        await vm.AddTagCommand.ExecuteAsync(null);
        await vm.ResetToCommitCommand.ExecuteAsync(null);
        await vm.RebaseOntoCommand.ExecuteAsync(null);

        Assert.Empty(git.Operations);
    }
}
