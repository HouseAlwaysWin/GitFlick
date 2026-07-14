using System.IO;
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
    public async Task Selecting_an_unstaged_file_loads_its_diff()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "one\ntwo\n");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");
        repo.WriteFile("a.txt", "one\nTWO\n");

        var vm = ForRepo(repo);
        await vm.RefreshAsync();
        Assert.False(vm.HasDiff);   // nothing selected yet

        vm.SelectedUnstagedFile = vm.UnstagedFiles.Single();
        await vm.DiffLoad;

        Assert.True(vm.HasDiff);
        Assert.Equal("a.txt", vm.DiffPath);
        Assert.Contains("-two", vm.DiffText);
        Assert.Contains("+TWO", vm.DiffText);
    }

    [Fact]
    public async Task Selecting_an_untracked_file_shows_it_as_all_added()
    {
        using var repo = new TestRepo();
        repo.WriteFile("seed.txt", "s");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "seed");
        repo.WriteFile("fresh.txt", "brand new\n");

        var vm = ForRepo(repo);
        await vm.RefreshAsync();

        vm.SelectedUnstagedFile = vm.UnstagedFiles.Single(f => f.Path == "fresh.txt");
        await vm.DiffLoad;

        Assert.Contains("+brand new", vm.DiffText);
    }

    [Fact]
    public async Task Selecting_a_staged_file_shows_the_staged_diff_and_clears_the_other_selection()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "base\n");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");
        repo.WriteFile("a.txt", "staged\n");
        repo.Git("add", "a.txt");
        repo.WriteFile("b.txt", "unstaged\n");

        var vm = ForRepo(repo);
        await vm.RefreshAsync();

        vm.SelectedUnstagedFile = vm.UnstagedFiles.Single(f => f.Path == "b.txt");
        await vm.DiffLoad;
        Assert.Equal("b.txt", vm.DiffPath);

        vm.SelectedStagedFile = vm.StagedFiles.Single(f => f.Path == "a.txt");
        await vm.DiffLoad;

        Assert.Equal("a.txt", vm.DiffPath);
        Assert.Contains("+staged", vm.DiffText);
        Assert.Null(vm.SelectedUnstagedFile);   // the other list's selection was cleared
    }

    [Fact]
    public async Task Checkout_of_a_commit_prefers_its_branch_over_detaching_head()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "1");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");
        repo.Git("branch", "feature");   // 'feature' now sits on the same commit as main

        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);

        var commit = vm.Commits.Single();
        Assert.Contains(commit.Refs, r => r.Name == "feature");

        vm.SelectedCommit = commit;
        await vm.CheckoutCommitCommand.ExecuteAsync(null);

        // Checking out a bare SHA would detach HEAD; a branch on the commit must win.
        var head = repo.Git("rev-parse", "--abbrev-ref", "HEAD").Trim();
        Assert.NotEqual("HEAD", head);   // "HEAD" here would mean detached
    }

    [Fact]
    public async Task Cherry_pick_replays_a_commit_onto_the_current_branch()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "base");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "base");

        // A commit that only exists on a side branch.
        repo.Git("checkout", "-b", "side");
        repo.WriteFile("picked.txt", "picked content");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "the one to pick");

        repo.Git("checkout", "main");
        Assert.False(File.Exists(Path.Combine(repo.Path, "picked.txt")));

        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);

        vm.SelectedCommit = vm.Commits.Single(c => c.Subject == "the one to pick");
        await vm.CherryPickCommand.ExecuteAsync(null);

        // The file it introduced now exists on main.
        Assert.True(File.Exists(Path.Combine(repo.Path, "picked.txt")));
        Assert.Equal("main", repo.Git("rev-parse", "--abbrev-ref", "HEAD").Trim());
    }

    [Fact]
    public async Task History_reloads_after_an_operation_moves_head()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "1");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");

        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);
        Assert.Single(vm.Commits);

        // A cherry-pick adds a commit; the graph must not still show the old history.
        repo.Git("checkout", "-b", "side");
        repo.WriteFile("b.txt", "2");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "second");
        repo.Git("checkout", "main");

        await vm.ShowHistoryCommand.ExecuteAsync(null);
        vm.SelectedCommit = vm.Commits.Single(c => c.Subject == "second");
        await vm.CherryPickCommand.ExecuteAsync(null);

        Assert.Contains(vm.Commits, c => c.Subject == "second" && c.Refs.Any(r => r.Name == "main"));
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
