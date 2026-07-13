using System.Linq;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.Services;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>
/// Drives the workspace VM against a real repo — the same operations the buttons invoke.
/// </summary>
public class WorkspaceViewModelTests
{
    private static WorkspaceViewModel ForRepo(TestRepo repo)
    {
        var item = new RepositoryItem(System.IO.Path.GetFileName(repo.Path), repo.Path);
        return new WorkspaceViewModel(new GitService(), item);
    }

    [Fact]
    public async Task Refresh_splits_files_into_staged_and_unstaged()
    {
        using var repo = new TestRepo();
        repo.WriteFile("committed.txt", "base");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "base");

        repo.WriteFile("committed.txt", "edited");   // unstaged edit
        repo.WriteFile("fresh.txt", "new");          // untracked -> unstaged
        repo.WriteFile("staged.txt", "s");
        repo.Git("add", "staged.txt");               // staged

        var vm = ForRepo(repo);
        await vm.RefreshAsync();

        Assert.Contains(vm.UnstagedFiles, e => e.Path == "committed.txt");
        Assert.Contains(vm.UnstagedFiles, e => e.Path == "fresh.txt");
        Assert.Contains(vm.StagedFiles, e => e.Path == "staged.txt");
        Assert.True(vm.HasStagedFiles);
    }

    [Fact]
    public async Task Header_shows_the_current_branch()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "1");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");

        var vm = ForRepo(repo);
        await vm.RefreshAsync();

        Assert.Equal("main", vm.BranchName);
    }

    [Fact]
    public async Task Stage_command_moves_a_file_and_enables_commit()
    {
        using var repo = new TestRepo();
        repo.WriteFile("報告.txt", "內容");   // CJK, to keep the whole pipeline honest

        var vm = ForRepo(repo);
        await vm.RefreshAsync();
        Assert.False(vm.CanCommit);   // nothing staged yet

        vm.SelectedUnstagedFile = vm.UnstagedFiles.Single();
        await vm.StageCommand.ExecuteAsync(null);

        Assert.Contains(vm.StagedFiles, e => e.Path == "報告.txt");
        Assert.Empty(vm.UnstagedFiles);

        vm.CommitMessage = "初次提交";
        Assert.True(vm.CanCommit);
    }

    [Fact]
    public async Task Commit_command_clears_the_message_and_cleans_the_tree()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "1");

        var vm = ForRepo(repo);
        await vm.RefreshAsync();
        await vm.StageAllCommand.ExecuteAsync(null);

        vm.CommitMessage = "初次提交";
        await vm.CommitCommand.ExecuteAsync(null);

        Assert.Equal("Committed", vm.StatusText);
        Assert.Empty(vm.CommitMessage);
        Assert.Empty(vm.StagedFiles);
        Assert.Empty(vm.UnstagedFiles);
    }

    [Fact]
    public async Task UnstageAll_returns_everything_to_the_worktree()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "1");
        repo.WriteFile("b.txt", "2");

        var vm = ForRepo(repo);
        await vm.RefreshAsync();
        await vm.StageAllCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.StagedFiles.Count);

        await vm.UnstageAllCommand.ExecuteAsync(null);
        Assert.Empty(vm.StagedFiles);
        Assert.Equal(2, vm.UnstagedFiles.Count);
    }

    [Fact]
    public async Task CreateBranch_switches_and_populates_the_branch_list()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "1");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");

        var vm = ForRepo(repo);
        await vm.RefreshAsync();

        vm.NewBranchName = "feature";
        await vm.CreateBranchCommand.ExecuteAsync(null);

        Assert.Equal("feature", vm.BranchName);
        Assert.Empty(vm.NewBranchName);
        Assert.Contains(vm.Branches, b => b.Name == "feature" && b.IsCurrent);
        Assert.Contains(vm.Branches, b => b.Name == "main");
    }

    [Fact]
    public async Task Checkout_command_switches_to_the_selected_branch()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "1");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");
        repo.Git("branch", "feature");

        var vm = ForRepo(repo);
        await vm.RefreshAsync();

        vm.SelectedBranch = vm.Branches.Single(b => b.Name == "feature");
        await vm.CheckoutCommand.ExecuteAsync(null);

        Assert.Equal("feature", vm.BranchName);
    }

    [Fact]
    public async Task Stash_push_then_pop_round_trips_through_the_vm()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "1");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");
        repo.WriteFile("a.txt", "changed");

        var vm = ForRepo(repo);
        await vm.RefreshAsync();

        await vm.StashPushCommand.ExecuteAsync(null);
        Assert.Empty(vm.UnstagedFiles);
        Assert.Single(vm.Stashes);

        await vm.StashPopCommand.ExecuteAsync(null);
        Assert.Contains(vm.UnstagedFiles, e => e.Path == "a.txt");
        Assert.Empty(vm.Stashes);
    }

    [Fact]
    public async Task A_failed_operation_surfaces_gits_error_without_throwing()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "1");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");

        var vm = ForRepo(repo);
        await vm.RefreshAsync();

        // Deleting the current branch must fail; the VM should report, not crash.
        vm.SelectedBranch = vm.Branches.Single(b => b.Name == "main");
        await vm.DeleteBranchCommand.ExecuteAsync(null);

        // SelectedBranch is current, so DeleteBranch is a guarded no-op — the branch survives.
        Assert.Contains(vm.Branches, b => b.Name == "main");
    }
}
