using System;
using System.IO;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.Services;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>
/// Characterization tests for the shared diff pane's staleness guard (LoadDiffAsync's token) and the
/// commit box's clear-on-success rule (RunAsync's bool return). These pin the behaviour the
/// refactors preserve: the old code detected commit success by string-comparing the localized
/// StatusText, and had no guard against a slow diff overwriting a newer one.
/// </summary>
public class DiffAndCommitBehaviorTests
{
    private static WorkspaceViewModel Workspace(out FakeGitService git)
    {
        git = new FakeGitService();
        return new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));
    }

    private static GitStatusEntry Unstaged(string path) => new()
    {
        Path = path,
        Kind = GitChangeKind.Ordinary,
        UnstagedState = GitFileState.Modified,
    };

    private static GitStatus WithOneStagedFile() => new()
    {
        BranchName = "main",
        Entries = [new GitStatusEntry { Path = "a.txt", Kind = GitChangeKind.Ordinary, StagedState = GitFileState.Modified }],
    };

    [Fact]
    public async Task Empty_diff_shows_the_localized_no_changes_placeholder()
    {
        var vm = Workspace(out _);   // FakeGitService.GetDiffAsync returns "" by default

        vm.SelectedUnstagedFile = Unstaged("a.txt");
        await vm.DiffLoad;

        Assert.Equal(LocalizationService.Instance["Diff_NoTextualChanges"], vm.DiffText);
    }

    [Fact]
    public async Task A_slower_earlier_diff_does_not_overwrite_a_faster_later_one()
    {
        var vm = Workspace(out var git);
        var slowGate = new TaskCompletionSource<string>();
        git.DiffOverride = path => path == "slow.txt" ? slowGate.Task : Task.FromResult("FAST DIFF");

        vm.SelectedUnstagedFile = Unstaged("slow.txt");   // starts the slow load (still pending)
        var slowLoad = vm.DiffLoad;

        vm.SelectedUnstagedFile = Unstaged("fast.txt");   // starts + finishes the fast load
        await vm.DiffLoad;
        Assert.Equal("FAST DIFF", vm.DiffText);

        slowGate.SetResult("SLOW DIFF");   // the slow load now completes, but it is stale
        await slowLoad;

        Assert.Equal("FAST DIFF", vm.DiffText);   // the stale result was dropped
        Assert.Equal("fast.txt", vm.DiffPath);
    }

    [Fact]
    public async Task Failed_commit_keeps_the_message_for_a_retry()
    {
        var vm = Workspace(out var git);
        git.StubStatus = WithOneStagedFile();
        await vm.RefreshAsync();   // populate HasStagedFiles so CanCommit is true

        vm.CommitMessage = "keep me";
        git.CommitResult = new GitCommandResult(1, string.Empty, "commit failed");

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.Equal("keep me", vm.CommitMessage);   // a failed commit preserves the message
    }

    [Fact]
    public async Task Successful_commit_clears_the_message()
    {
        var vm = Workspace(out var git);
        git.StubStatus = WithOneStagedFile();
        await vm.RefreshAsync();

        vm.CommitMessage = "ship it";
        await vm.CommitCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.CommitMessage);   // success clears (no template configured)
    }
}
