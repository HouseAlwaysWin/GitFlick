using System.Linq;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.Services;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>The two History filters the workspace exposes: fuzzy message search and by-file.</summary>
public class HistoryFilterTests
{
    private static WorkspaceViewModel ForRepo(TestRepo repo)
    {
        var item = new RepositoryItem(System.IO.Path.GetFileName(repo.Path), repo.Path);
        return new WorkspaceViewModel(new GitService(), item);
    }

    private static TestRepo BuildRepo()
    {
        var repo = new TestRepo();
        repo.WriteFile("app.cs", "1"); repo.Git("add", "-A"); repo.Git("commit", "-m", "feat: add login");
        repo.WriteFile("app.cs", "2"); repo.Git("add", "-A"); repo.Git("commit", "-m", "fix: login redirect");
        repo.WriteFile("readme.md", "hi"); repo.Git("add", "-A"); repo.Git("commit", "-m", "docs: update readme");
        repo.WriteFile("report.txt", "內容"); repo.Git("add", "-A"); repo.Git("commit", "-m", "新增報告");
        return repo;
    }

    [Fact]
    public async Task Message_filter_shows_only_matching_commits()
    {
        using var repo = BuildRepo();
        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);
        Assert.Equal(4, vm.Commits.Count);

        vm.MessageFilter = "login";

        Assert.Equal(2, vm.Commits.Count);
        Assert.All(vm.Commits, c => Assert.Contains("login", c.Subject));
        Assert.True(vm.HasMessageFilter);
        Assert.False(vm.ShowGraph);   // a message subset isn't parent-closed → no lane graph
    }

    [Fact]
    public async Task Message_filter_is_fuzzy_not_just_substring()
    {
        using var repo = BuildRepo();
        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);

        // "flgn" is a subsequence of "fix: login redirect" / "feat: add login" but not a substring.
        vm.MessageFilter = "lgn";

        Assert.NotEmpty(vm.Commits);
        Assert.All(vm.Commits, c => Assert.Contains("login", c.Subject));
    }

    [Fact]
    public async Task Message_filter_matches_cjk_subjects()
    {
        using var repo = BuildRepo();
        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);

        vm.MessageFilter = "報告";

        var only = Assert.Single(vm.Commits);
        Assert.Equal("新增報告", only.Subject);
    }

    [Fact]
    public async Task Clearing_the_message_filter_restores_the_full_history_and_graph()
    {
        using var repo = BuildRepo();
        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);

        vm.MessageFilter = "login";
        Assert.Equal(2, vm.Commits.Count);

        vm.MessageFilter = "";

        Assert.Equal(4, vm.Commits.Count);
        Assert.False(vm.HasMessageFilter);
        Assert.True(vm.ShowGraph);
    }

    [Fact]
    public async Task File_filter_shows_only_commits_that_touched_the_path()
    {
        using var repo = BuildRepo();
        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);
        Assert.Equal(4, vm.Commits.Count);

        // app.cs was touched by exactly the two login commits.
        vm.FileFilter = "app.cs";
        await vm.HistoryLoad;

        Assert.Equal(2, vm.Commits.Count);
        Assert.All(vm.Commits, c => Assert.Contains("login", c.Subject));
        Assert.True(vm.HasFileFilter);
        Assert.False(vm.ShowGraph);
    }

    [Fact]
    public async Task File_filter_handles_a_cjk_path()
    {
        var repo = new TestRepo();
        repo.WriteFile("其他.txt", "x"); repo.Git("add", "-A"); repo.Git("commit", "-m", "other");
        repo.WriteFile("報告.txt", "1"); repo.Git("add", "-A"); repo.Git("commit", "-m", "add report");
        repo.WriteFile("報告.txt", "2"); repo.Git("add", "-A"); repo.Git("commit", "-m", "edit report");

        using (repo)
        {
            var vm = ForRepo(repo);
            await vm.ShowHistoryCommand.ExecuteAsync(null);

            vm.FileFilter = "報告.txt";
            await vm.HistoryLoad;

            Assert.Equal(2, vm.Commits.Count);
            Assert.All(vm.Commits, c => Assert.Contains("report", c.Subject));
        }
    }

    [Fact]
    public async Task File_filter_accepts_a_glob()
    {
        using var repo = BuildRepo();
        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);

        vm.FileFilter = "*.md";
        await vm.HistoryLoad;

        var only = Assert.Single(vm.Commits);
        Assert.Equal("docs: update readme", only.Subject);
    }

    [Fact]
    public async Task Clearing_the_file_filter_reloads_the_full_history()
    {
        using var repo = BuildRepo();
        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);

        vm.FileFilter = "app.cs";
        await vm.HistoryLoad;
        Assert.Equal(2, vm.Commits.Count);

        vm.FileFilter = string.Empty;
        await vm.HistoryLoad;

        Assert.Equal(4, vm.Commits.Count);
        Assert.False(vm.HasFileFilter);
        Assert.True(vm.ShowGraph);
    }

    [Fact]
    public async Task The_two_filters_compose()
    {
        using var repo = BuildRepo();
        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);

        // File narrows to the two app.cs commits; message then narrows those to the "fix" one.
        vm.FileFilter = "app.cs";
        await vm.HistoryLoad;
        vm.MessageFilter = "redirect";

        var only = Assert.Single(vm.Commits);
        Assert.Equal("fix: login redirect", only.Subject);
    }

    // ── The unified search box (Message / File scope toggle) ────────────────────

    [Fact]
    public async Task Search_defaults_to_message_scope_and_filters_live()
    {
        using var repo = BuildRepo();
        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);

        Assert.Equal(HistorySearchType.Message, vm.SearchType);
        Assert.True(vm.IsMessageSearch);
        Assert.Empty(vm.FilteredPathSuggestions);   // Message scope shows no path pick list.

        vm.SearchText = "login";

        Assert.Equal("login", vm.MessageFilter);
        Assert.Equal(2, vm.Commits.Count);
        Assert.All(vm.Commits, c => Assert.Contains("login", c.Subject));
    }

    [Fact]
    public async Task Switching_to_file_scope_loads_every_historical_path()
    {
        using var repo = BuildRepo();
        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);

        await vm.UseFileSearchCommand.ExecuteAsync(null);

        Assert.True(vm.IsFileSearch);
        Assert.Contains("app.cs", vm.PathSuggestions);
        Assert.Contains("readme.md", vm.PathSuggestions);
        Assert.Contains("report.txt", vm.PathSuggestions);
        // With an empty query the pick list shows every loaded path.
        Assert.Contains("app.cs", vm.FilteredPathSuggestions);
    }

    [Fact]
    public async Task File_pick_list_narrows_by_fuzzy_query()
    {
        using var repo = BuildRepo();
        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);
        await vm.UseFileSearchCommand.ExecuteAsync(null);

        vm.SearchText = "app";   // narrows the pick list; doesn't touch the commit list yet

        Assert.Contains("app.cs", vm.FilteredPathSuggestions);
        Assert.DoesNotContain("readme.md", vm.FilteredPathSuggestions);
        Assert.Equal(4, vm.Commits.Count);       // still whole — nothing applied
        Assert.False(vm.HasFileFilter);
    }

    [Fact]
    public async Task Picking_a_path_applies_it_as_the_file_filter()
    {
        using var repo = BuildRepo();
        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);
        await vm.UseFileSearchCommand.ExecuteAsync(null);

        vm.PickPath("app.cs");
        await vm.HistoryLoad;

        Assert.Equal("app.cs", vm.SearchText);
        Assert.Equal("app.cs", vm.FileFilter);
        Assert.Equal(2, vm.Commits.Count);
        Assert.All(vm.Commits, c => Assert.Contains("login", c.Subject));
    }

    [Fact]
    public async Task Picking_from_a_multi_match_list_leaves_the_pick_list_intact()
    {
        // Regression: picking used to re-narrow the suggestions from inside the ListBox's own
        // SelectionChanged, clearing the list mid-selection → ArgumentOutOfRangeException. PickPath
        // must echo the path without disturbing the list.
        var repo = new TestRepo();
        repo.WriteFile("src/app.cs", "1"); repo.Git("add", "-A"); repo.Git("commit", "-m", "a");
        repo.WriteFile("src/login.cs", "1"); repo.Git("add", "-A"); repo.Git("commit", "-m", "b");
        repo.WriteFile("src/legacy.cs", "1"); repo.Git("add", "-A"); repo.Git("commit", "-m", "c");

        using (repo)
        {
            var vm = ForRepo(repo);
            await vm.ShowHistoryCommand.ExecuteAsync(null);
            await vm.UseFileSearchCommand.ExecuteAsync(null);

            vm.SearchText = "src";
            var before = vm.FilteredPathSuggestions.Count;
            Assert.True(before >= 3);

            vm.PickPath("src/login.cs");   // a non-first item — the crash case

            Assert.Equal(before, vm.FilteredPathSuggestions.Count);   // list untouched
            Assert.Equal("src/login.cs", vm.SearchText);
            await vm.HistoryLoad;
            Assert.Equal("src/login.cs", vm.FileFilter);
        }
    }

    [Fact]
    public async Task Enter_in_file_scope_applies_the_typed_path()
    {
        using var repo = BuildRepo();
        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);
        await vm.UseFileSearchCommand.ExecuteAsync(null);

        // Typing a path doesn't hit git yet — the list is still whole.
        vm.SearchText = "app.cs";
        Assert.Equal(4, vm.Commits.Count);
        Assert.False(vm.HasFileFilter);

        // Enter commits it as the git-level filter (ApplySearch).
        vm.ApplySearchCommand.Execute(null);
        await vm.HistoryLoad;

        Assert.Equal(2, vm.Commits.Count);
        Assert.True(vm.HasFileFilter);
        Assert.All(vm.Commits, c => Assert.Contains("login", c.Subject));
    }

    [Fact]
    public async Task Switching_scope_clears_the_previous_scopes_filter()
    {
        using var repo = BuildRepo();
        var vm = ForRepo(repo);
        await vm.ShowHistoryCommand.ExecuteAsync(null);

        // A live message filter is dropped when we move to File scope.
        vm.SearchText = "login";
        Assert.Equal(2, vm.Commits.Count);

        await vm.UseFileSearchCommand.ExecuteAsync(null);
        Assert.Equal(string.Empty, vm.SearchText);
        Assert.False(vm.HasMessageFilter);
        Assert.Equal(4, vm.Commits.Count);

        // An applied file filter is dropped when we move back to Message scope.
        vm.SearchText = "app.cs";
        vm.ApplySearchCommand.Execute(null);
        await vm.HistoryLoad;
        Assert.Equal(2, vm.Commits.Count);

        await vm.UseMessageSearchCommand.ExecuteAsync(null);
        await vm.HistoryLoad;
        Assert.False(vm.HasFileFilter);
        Assert.Equal(4, vm.Commits.Count);
        Assert.True(vm.ShowGraph);   // back to a clean, parent-closed history → lane graph returns
    }

    [Fact]
    public async Task Historical_paths_include_a_deleted_file()
    {
        var repo = new TestRepo();
        repo.WriteFile("keep.txt", "1"); repo.Git("add", "-A"); repo.Git("commit", "-m", "add keep");
        repo.WriteFile("gone.txt", "x"); repo.Git("add", "-A"); repo.Git("commit", "-m", "add gone");
        repo.Git("rm", "gone.txt"); repo.Git("commit", "-m", "remove gone");

        using (repo)
        {
            var vm = ForRepo(repo);
            await vm.ShowHistoryCommand.ExecuteAsync(null);
            await vm.UseFileSearchCommand.ExecuteAsync(null);

            // gone.txt no longer exists in the tree, but it lived in history — so it's suggestable.
            Assert.Contains("gone.txt", vm.PathSuggestions);
            Assert.Contains("keep.txt", vm.PathSuggestions);
        }
    }
}
