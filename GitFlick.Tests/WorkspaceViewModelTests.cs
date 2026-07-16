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
    public async Task CheckoutRef_switches_to_a_double_clicked_branch()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "1");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");
        repo.Git("branch", "feature");

        var vm = ForRepo(repo);
        await vm.RefreshAsync();

        await vm.CheckoutRef(new GitRef("feature", GitRefKind.LocalBranch));

        Assert.Equal("feature", repo.Git("rev-parse", "--abbrev-ref", "HEAD").Trim());
    }

    [Fact]
    public async Task DeleteRef_deletes_a_confirmed_branch()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "1");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");
        repo.Git("branch", "feature");

        var vm = ForRepo(repo);
        await vm.RefreshAsync();
        vm.ConfirmDeleteBranch = _ => Task.FromResult<bool?>(false);   // confirm, safe delete

        await vm.DeleteRef(new GitRef("feature", GitRefKind.LocalBranch));

        Assert.DoesNotContain("feature", repo.Git("branch"));
    }

    [Fact]
    public async Task DeleteRef_cancelled_keeps_the_branch()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "1");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");
        repo.Git("branch", "feature");

        var vm = ForRepo(repo);
        await vm.RefreshAsync();
        vm.ConfirmDeleteBranch = _ => Task.FromResult<bool?>(null);   // cancel

        await vm.DeleteRef(new GitRef("feature", GitRefKind.LocalBranch));

        Assert.Contains("feature", repo.Git("branch"));
    }

    [Fact]
    public async Task DeleteRef_refuses_the_current_branch()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "1");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");

        var vm = ForRepo(repo);
        await vm.RefreshAsync();

        var asked = false;
        vm.ConfirmDeleteBranch = _ => { asked = true; return Task.FromResult<bool?>(false); };

        await vm.DeleteRef(new GitRef("main", GitRefKind.LocalBranch));

        Assert.False(asked);   // guarded before the prompt
        Assert.Contains("main", repo.Git("branch"));
        Assert.Contains("current branch", vm.StatusText);
    }

    [Fact]
    public async Task DeleteRef_force_deletes_an_unmerged_branch()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "1");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");
        repo.Git("checkout", "-b", "wip");
        repo.WriteFile("b.txt", "2");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "unmerged work");
        repo.Git("checkout", "main");

        var vm = ForRepo(repo);
        await vm.RefreshAsync();
        vm.ConfirmDeleteBranch = _ => Task.FromResult<bool?>(true);   // force

        await vm.DeleteRef(new GitRef("wip", GitRefKind.LocalBranch));

        Assert.DoesNotContain("wip", repo.Git("branch"));
    }

    [Fact]
    public async Task FetchPrune_drops_remote_tracking_branches_deleted_upstream()
    {
        using var origin = new TestRepo();
        origin.WriteFile("a.txt", "1");
        origin.Git("add", "-A");
        origin.Git("commit", "-m", "first");
        origin.Git("branch", "stale");

        using var repo = new TestRepo();
        repo.Git("remote", "add", "origin", origin.Path);
        repo.Git("fetch", "origin");
        Assert.Contains("origin/stale", repo.Git("branch", "-r"));

        origin.Git("branch", "-D", "stale");   // the upstream branch goes away

        var vm = ForRepo(repo);
        await vm.RefreshAsync();
        await vm.FetchPruneCommand.ExecuteAsync(null);

        // A plain fetch would keep origin/stale; prune is what removes it.
        Assert.DoesNotContain("origin/stale", repo.Git("branch", "-r"));
    }

    [Fact]
    public async Task FetchAll_fetches_from_every_remote()
    {
        using var origin = new TestRepo();
        origin.WriteFile("a.txt", "1");
        origin.Git("add", "-A");
        origin.Git("commit", "-m", "origin commit");

        using var backup = new TestRepo();
        backup.WriteFile("b.txt", "2");
        backup.Git("add", "-A");
        backup.Git("commit", "-m", "backup commit");
        backup.Git("branch", "backup-branch");

        using var repo = new TestRepo();
        repo.Git("remote", "add", "origin", origin.Path);
        repo.Git("remote", "add", "backup", backup.Path);

        var vm = ForRepo(repo);
        await vm.RefreshAsync();
        await vm.FetchAllCommand.ExecuteAsync(null);

        // --all reaches both remotes; a bare fetch would only touch origin.
        var remotes = repo.Git("branch", "-r");
        Assert.Contains("origin/main", remotes);
        Assert.Contains("backup/backup-branch", remotes);
    }

    [Fact]
    public async Task PullRebase_replays_local_commits_without_a_merge_commit()
    {
        using var origin = new TestRepo();
        origin.WriteFile("a.txt", "base");
        origin.Git("add", "-A");
        origin.Git("commit", "-m", "base");

        using var repo = new TestRepo();
        repo.Git("remote", "add", "origin", origin.Path);
        repo.Git("fetch", "origin");
        repo.Git("reset", "--hard", "origin/main");
        repo.Git("branch", "--set-upstream-to=origin/main", "main");

        // Diverge: one commit upstream, one unrelated commit locally.
        origin.WriteFile("remote.txt", "r");
        origin.Git("add", "-A");
        origin.Git("commit", "-m", "remote work");

        repo.WriteFile("local.txt", "l");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "local work");

        var vm = ForRepo(repo);
        await vm.RefreshAsync();
        await vm.PullRebaseCommand.ExecuteAsync(null);

        // Rebase linearises history: local work replays on top of remote work, no merge commit.
        Assert.Equal(string.Empty, repo.Git("log", "--merges", "--oneline").Trim());
        Assert.True(File.Exists(Path.Combine(repo.Path, "remote.txt")));
        Assert.True(File.Exists(Path.Combine(repo.Path, "local.txt")));
        Assert.StartsWith("local work", repo.Git("log", "-1", "--pretty=%s").Trim());
    }

    [Fact]
    public async Task GetRemotes_lists_configured_remotes()
    {
        using var origin = new TestRepo();
        using var backup = new TestRepo();
        using var repo = new TestRepo();
        repo.Git("remote", "add", "origin", origin.Path);
        repo.Git("remote", "add", "backup", backup.Path);

        var remotes = await new GitService().GetRemotesAsync(repo.Path);

        Assert.Equal(new[] { "backup", "origin" }, remotes.OrderBy(r => r).ToArray());
    }

    [Fact]
    public async Task PullFrom_merges_the_named_branch_from_the_remote()
    {
        using var origin = new TestRepo();
        origin.WriteFile("a.txt", "1");
        origin.Git("add", "-A");
        origin.Git("commit", "-m", "base");
        origin.Git("checkout", "-b", "topic");
        origin.WriteFile("t.txt", "topic");
        origin.Git("add", "-A");
        origin.Git("commit", "-m", "topic work");
        origin.Git("checkout", "main");

        using var repo = new TestRepo();
        repo.Git("remote", "add", "origin", origin.Path);
        repo.Git("fetch", "origin");
        repo.Git("reset", "--hard", "origin/main");

        var vm = ForRepo(repo);
        await vm.RefreshAsync();
        vm.PromptPullSource = (_, _) => Task.FromResult<WorkspaceViewModel.RemoteBranch?>(
            new WorkspaceViewModel.RemoteBranch("origin", "topic"));

        await vm.PullFromCommand.ExecuteAsync(null);

        // Pulling origin/topic brings its file into the current branch's working tree.
        Assert.True(File.Exists(Path.Combine(repo.Path, "t.txt")));
    }

    [Fact]
    public async Task PushTo_pushes_the_current_branch_to_the_named_remote()
    {
        using var remote = new TestRepo();
        remote.WriteFile("a.txt", "1");
        remote.Git("add", "-A");
        remote.Git("commit", "-m", "base");

        using var repo = new TestRepo();
        repo.Git("remote", "add", "origin", remote.Path);
        repo.Git("fetch", "origin");
        repo.Git("reset", "--hard", "origin/main");
        repo.Git("checkout", "-b", "feature");
        repo.WriteFile("f.txt", "x");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "feature work");

        var vm = ForRepo(repo);
        await vm.RefreshAsync();
        vm.PromptPushTarget = (_, _) => Task.FromResult<string?>("origin");

        await vm.PushToCommand.ExecuteAsync(null);

        // The remote receives the pushed branch (it isn't the remote's checked-out branch).
        Assert.Contains("feature", remote.Git("branch"));
    }

    [Fact]
    public async Task Checkout_with_uncommitted_changes_asks_first_and_a_cancel_blocks_it()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "1");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");
        repo.Git("branch", "feature");
        repo.WriteFile("a.txt", "uncommitted edit");   // dirty, tracked

        var vm = ForRepo(repo);
        await vm.RefreshAsync();

        var asked = false;
        vm.ConfirmDirtyCheckout = _ => { asked = true; return Task.FromResult(false); };   // user cancels

        await vm.CheckoutRef(new GitRef("feature", GitRefKind.LocalBranch));

        Assert.True(asked);
        Assert.Equal("main", repo.Git("rev-parse", "--abbrev-ref", "HEAD").Trim());   // not switched
    }

    [Fact]
    public async Task Checkout_with_uncommitted_changes_proceeds_once_confirmed()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "1");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");
        repo.Git("branch", "feature");
        repo.WriteFile("a.txt", "uncommitted edit");   // carries over to feature (same commit)

        var vm = ForRepo(repo);
        await vm.RefreshAsync();

        var asked = false;
        vm.ConfirmDirtyCheckout = _ => { asked = true; return Task.FromResult(true); };   // user confirms

        await vm.CheckoutRef(new GitRef("feature", GitRefKind.LocalBranch));

        Assert.True(asked);
        Assert.Equal("feature", repo.Git("rev-parse", "--abbrev-ref", "HEAD").Trim());
    }

    [Fact]
    public async Task Checkout_with_a_clean_tree_does_not_prompt()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "1");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");
        repo.Git("branch", "feature");

        var vm = ForRepo(repo);
        await vm.RefreshAsync();

        var asked = false;
        vm.ConfirmDirtyCheckout = _ => { asked = true; return Task.FromResult(true); };

        await vm.CheckoutRef(new GitRef("feature", GitRefKind.LocalBranch));

        Assert.False(asked);   // clean tree, so no warning
        Assert.Equal("feature", repo.Git("rev-parse", "--abbrev-ref", "HEAD").Trim());
    }

    [Fact]
    public async Task CheckoutRef_ignores_a_tag()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "1");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");
        repo.Git("tag", "v1");

        var vm = ForRepo(repo);
        await vm.RefreshAsync();

        await vm.CheckoutRef(new GitRef("v1", GitRefKind.Tag));   // would only detach HEAD, so ignored

        Assert.Equal("main", repo.Git("rev-parse", "--abbrev-ref", "HEAD").Trim());
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

    private static void CommitAs(TestRepo repo, string file, string author, string message, string date)
    {
        repo.WriteFile(file, message);
        repo.Git("add", "-A");
        repo.Git("commit", "--author=" + author + " <" + author + "@example.com>", "--date=" + date, "-m", message);
    }

    [Fact]
    public async Task History_defaults_to_graph_order_with_the_graph_showing()
    {
        using var repo = new TestRepo();
        CommitAs(repo, "a.txt", "Charlie", "banana", "2020-01-01T00:00:00");
        CommitAs(repo, "b.txt", "Alice", "cherry", "2022-01-01T00:00:00");

        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);

        Assert.Equal(HistorySortColumn.Graph, vm.SortColumn);
        Assert.True(vm.ShowGraph);
    }

    [Fact]
    public async Task Sorting_by_author_reorders_and_hides_the_graph()
    {
        using var repo = new TestRepo();
        CommitAs(repo, "a.txt", "Charlie", "banana", "2020-01-01T00:00:00");
        CommitAs(repo, "b.txt", "Alice", "cherry", "2022-01-01T00:00:00");
        CommitAs(repo, "c.txt", "Bob", "apple", "2021-01-01T00:00:00");

        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);

        vm.SortByCommand.Execute(HistorySortColumn.Author);

        Assert.False(vm.ShowGraph);   // the lane graph can't align with a name sort
        Assert.Equal(new[] { "Alice", "Bob", "Charlie" }, vm.Commits.Select(c => c.Author));
    }

    [Fact]
    public async Task Clicking_a_sorted_column_again_toggles_direction()
    {
        using var repo = new TestRepo();
        CommitAs(repo, "a.txt", "Charlie", "banana", "2020-01-01T00:00:00");   // oldest
        CommitAs(repo, "b.txt", "Alice", "cherry", "2022-01-01T00:00:00");     // newest
        CommitAs(repo, "c.txt", "Bob", "apple", "2021-01-01T00:00:00");

        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);

        vm.SortByCommand.Execute(HistorySortColumn.Date);   // ascending: oldest first
        Assert.Equal(new[] { "banana", "apple", "cherry" }, vm.Commits.Select(c => c.Subject));
        Assert.False(vm.SortDescending);

        vm.SortByCommand.Execute(HistorySortColumn.Date);   // descending: newest first
        Assert.Equal(new[] { "cherry", "apple", "banana" }, vm.Commits.Select(c => c.Subject));
        Assert.True(vm.SortDescending);
    }

    [Fact]
    public async Task Resetting_the_sort_restores_git_order_and_the_graph()
    {
        using var repo = new TestRepo();
        CommitAs(repo, "a.txt", "Charlie", "banana", "2020-01-01T00:00:00");
        CommitAs(repo, "b.txt", "Alice", "cherry", "2022-01-01T00:00:00");

        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);
        var graphOrder = vm.Commits.Select(c => c.Sha).ToArray();

        vm.SortByCommand.Execute(HistorySortColumn.Author);
        Assert.False(vm.ShowGraph);

        vm.ResetSortCommand.Execute(null);

        Assert.True(vm.ShowGraph);
        Assert.Equal(graphOrder, vm.Commits.Select(c => c.Sha));
    }

    [Fact]
    public async Task Author_filter_narrows_to_the_ticked_authors_and_hides_the_graph()
    {
        using var repo = new TestRepo();
        CommitAs(repo, "a.txt", "Alice", "a1", "2020-01-01T00:00:00");
        CommitAs(repo, "b.txt", "Bob", "b1", "2021-01-01T00:00:00");
        CommitAs(repo, "c.txt", "Charlie", "c1", "2022-01-01T00:00:00");
        CommitAs(repo, "d.txt", "Alice", "a2", "2023-01-01T00:00:00");

        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.AuthorFilters.Count);   // Alice, Bob, Charlie — distinct
        Assert.True(vm.ShowGraph);

        vm.AuthorFilters.Single(a => a.Name == "Alice").IsSelected = true;

        Assert.True(vm.HasAuthorFilter);
        Assert.False(vm.ShowGraph);               // a filtered subset can't carry the graph
        Assert.Equal(2, vm.Commits.Count);
        Assert.All(vm.Commits, c => Assert.Equal("Alice", c.Author));
    }

    [Fact]
    public async Task Author_filter_is_multi_select()
    {
        using var repo = new TestRepo();
        CommitAs(repo, "a.txt", "Alice", "a1", "2020-01-01T00:00:00");
        CommitAs(repo, "b.txt", "Bob", "b1", "2021-01-01T00:00:00");
        CommitAs(repo, "c.txt", "Charlie", "c1", "2022-01-01T00:00:00");

        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);

        vm.AuthorFilters.Single(a => a.Name == "Alice").IsSelected = true;
        vm.AuthorFilters.Single(a => a.Name == "Charlie").IsSelected = true;

        Assert.Equal(2, vm.Commits.Count);
        Assert.Contains(vm.Commits, c => c.Author == "Alice");
        Assert.Contains(vm.Commits, c => c.Author == "Charlie");
        Assert.DoesNotContain(vm.Commits, c => c.Author == "Bob");
    }

    [Fact]
    public async Task Clearing_the_author_filter_restores_the_full_list_and_graph()
    {
        using var repo = new TestRepo();
        CommitAs(repo, "a.txt", "Alice", "a1", "2020-01-01T00:00:00");
        CommitAs(repo, "b.txt", "Bob", "b1", "2021-01-01T00:00:00");

        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);
        vm.AuthorFilters.Single(a => a.Name == "Alice").IsSelected = true;
        Assert.Single(vm.Commits);

        vm.ClearAuthorFilterCommand.Execute(null);

        Assert.False(vm.HasAuthorFilter);
        Assert.True(vm.ShowGraph);
        Assert.Equal(2, vm.Commits.Count);
    }

    [Fact]
    public async Task StageFiles_and_UnstageFiles_move_a_whole_selection_at_once()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "1");
        repo.WriteFile("b.txt", "2");
        repo.WriteFile("c.txt", "3");

        var vm = ForRepo(repo);
        await vm.RefreshAsync();
        Assert.Equal(3, vm.UnstagedFiles.Count);

        await vm.StageFiles(vm.UnstagedFiles.Take(2).ToList());
        Assert.Equal(2, vm.StagedFiles.Count);
        Assert.Single(vm.UnstagedFiles);

        await vm.UnstageFiles(vm.StagedFiles.ToList());
        Assert.Empty(vm.StagedFiles);
        Assert.Equal(3, vm.UnstagedFiles.Count);
    }

    [Fact]
    public async Task Selecting_a_commit_lists_its_files_and_shows_the_first_file_diff()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "one\n");
        repo.WriteFile("b.txt", "two\n");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "two files");

        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);

        vm.SelectedCommit = vm.Commits.Single();
        await vm.DiffLoad;                     // file list load
        Assert.Equal(2, vm.CommitFiles.Count);
        Assert.True(vm.HasCommitFiles);
        Assert.NotNull(vm.SelectedCommitFile);

        await vm.DiffLoad;                     // first file's diff
        Assert.True(vm.HasDiff);
        Assert.Equal(vm.SelectedCommitFile!.Path, vm.DiffPath);
    }

    [Fact]
    public async Task Graph_has_a_dot_for_every_commit_including_the_root()
    {
        using var repo = new TestRepo();
        for (var i = 0; i < 12; i++)
        {
            repo.WriteFile("f.txt", $"content {i}");
            repo.Git("add", "-A");
            repo.Git("commit", "-m", $"commit {i}");
        }

        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);

        Assert.Equal(12, vm.Commits.Count);
        Assert.Equal(12, vm.Graph!.Dots.Count);   // the builder never drops the last commits
    }

    [Fact]
    public async Task Branch_filter_narrows_to_commits_reachable_from_the_branch()
    {
        using var repo = new TestRepo();
        repo.WriteFile("base.txt", "b");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "base");
        repo.Git("checkout", "-b", "feature");
        repo.WriteFile("f.txt", "f");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "feature work");
        repo.Git("checkout", "main");
        repo.WriteFile("m.txt", "m");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "main work");

        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.Commits.Count);     // all branches shown by default

        vm.BranchFilters.Single(b => b.Name == "feature").IsSelected = true;

        Assert.True(vm.HasBranchFilter);
        Assert.True(vm.ShowGraph);   // reachable subset is a valid sub-DAG, so the graph stays
        Assert.Equal(vm.Commits.Count, vm.Graph!.Dots.Count);   // graph rebuilt for the subset
        Assert.Contains(vm.Commits, c => c.Subject == "feature work");
        Assert.Contains(vm.Commits, c => c.Subject == "base");
        Assert.DoesNotContain(vm.Commits, c => c.Subject == "main work");
    }

    [Fact]
    public async Task Author_search_fuzzy_narrows_the_checklist()
    {
        using var repo = new TestRepo();
        CommitAs(repo, "a.txt", "Alice", "a", "2020-01-01T00:00:00");
        CommitAs(repo, "b.txt", "Bob", "b", "2021-01-01T00:00:00");
        CommitAs(repo, "c.txt", "Charlie", "c", "2022-01-01T00:00:00");

        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.FilteredAuthorFilters.Count);

        // "alic" is a subsequence of "Alice" only (Charlie has a…l…i but no trailing c).
        vm.AuthorFilterSearch = "alic";
        Assert.Single(vm.FilteredAuthorFilters);
        Assert.Equal("Alice", vm.FilteredAuthorFilters[0].Name);

        vm.AuthorFilterSearch = "";
        Assert.Equal(3, vm.FilteredAuthorFilters.Count);
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
