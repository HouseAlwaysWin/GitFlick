using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.Services;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>
/// The VS Code-style search upgrades: a separate exclude input that combines with the include box,
/// Aa / .* modifiers per scope (Message client-side; Content via git), the File scope's
/// ":(exclude)" pathspec, and the History column clamp that keeps the table inside a narrow pane.
/// </summary>
public class HistorySearchModifierTests
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

    private static async Task<(WorkspaceViewModel Vm, FakeGitService Git)> LoadedWorkspace()
    {
        var git = new FakeGitService();
        git.StubCommits.Add(Commit(0, "feat: add login"));
        git.StubCommits.Add(Commit(1, "fix: login redirect"));
        git.StubCommits.Add(Commit(2, "docs: update readme"));
        git.StubCommits.Add(Commit(3, "Fix CI pipeline"));

        var vm = new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));
        await vm.History.LoadHistoryAsync();
        return (vm, git);
    }

    // ── Query and path scoping are independent filters ───────────────────────────

    [Fact]
    public async Task The_query_and_the_path_boxes_reach_git_independently()
    {
        var (vm, git) = await LoadedWorkspace();

        // Content is the scope that carries all three: a pickaxe query plus both path boxes.
        await vm.History.UseContentSearchCommand.ExecuteAsync(null);
        vm.History.ContentFilter = "TODO";
        vm.History.IncludeText = "src/";
        vm.History.ApplyIncludeCommand.Execute(null);
        vm.History.ExcludeText = "*.md";
        vm.History.ApplyExcludeCommand.Execute(null);
        await vm.History.HistoryLoad;

        Assert.Equal("TODO", git.LastContentSearch);
        Assert.Equal("src/", git.LastPathFilter);
        Assert.Equal("*.md", git.LastPathExclude);
    }

    [Fact]
    public async Task Clearing_the_search_clears_the_query_and_both_path_boxes()
    {
        var (vm, _) = await LoadedWorkspace();
        await vm.History.UseContentSearchCommand.ExecuteAsync(null);
        vm.History.ContentFilter = "TODO";
        vm.History.IncludeText = "src/";
        vm.History.ApplyIncludeCommand.Execute(null);
        vm.History.ExcludeText = "*.md";
        vm.History.ApplyExcludeCommand.Execute(null);
        await vm.History.HistoryLoad;

        vm.History.ClearSearchCommand.Execute(null);
        await vm.History.HistoryLoad;

        Assert.Equal(4, vm.History.Commits.Count);
        Assert.False(vm.History.HasSearchFilter);
        Assert.True(vm.History.ShowGraph);
    }

    [Fact]
    public async Task Regex_mode_matches_anchored_patterns()
    {
        var (vm, _) = await LoadedWorkspace();

        vm.History.SearchUseRegex = true;
        vm.History.MessageFilter = "^fix";           // case-insensitive by default

        Assert.Equal(2, vm.History.Commits.Count);   // "fix: login redirect" + "Fix CI pipeline"
        Assert.False(vm.History.SearchRegexInvalid);
    }

    [Fact]
    public async Task Regex_mode_honours_case_sensitivity()
    {
        var (vm, _) = await LoadedWorkspace();

        vm.History.SearchUseRegex = true;
        vm.History.SearchCaseSensitive = true;
        vm.History.MessageFilter = "^fix";

        var only = Assert.Single(vm.History.Commits);
        Assert.Equal("fix: login redirect", only.Subject);
    }

    [Fact]
    public async Task Fuzzy_matching_honours_case_sensitivity()
    {
        var (vm, _) = await LoadedWorkspace();

        vm.History.MessageFilter = "Fix";
        Assert.Equal(2, vm.History.Commits.Count);   // insensitive: both fix commits

        vm.History.SearchCaseSensitive = true;

        var only = Assert.Single(vm.History.Commits);
        Assert.Equal("Fix CI pipeline", only.Subject);
    }

    [Fact]
    public async Task An_invalid_regex_filters_nothing_and_flags_the_error()
    {
        var (vm, _) = await LoadedWorkspace();

        vm.History.SearchUseRegex = true;
        vm.History.MessageFilter = "([";             // doesn't parse

        Assert.Equal(4, vm.History.Commits.Count);   // unfiltered, not blanked
        Assert.True(vm.History.SearchRegexInvalid);

        vm.History.MessageFilter = "fix";            // now it parses

        Assert.False(vm.History.SearchRegexInvalid);
        Assert.Equal(2, vm.History.Commits.Count);
    }

    [Fact]
    public async Task The_search_label_shows_the_query_and_the_excluded_paths()
    {
        var (vm, _) = await LoadedWorkspace();

        await vm.History.UseContentSearchCommand.ExecuteAsync(null);
        vm.History.ContentFilter = "TODO";
        vm.History.ExcludeText = "*.md";
        vm.History.ApplyExcludeCommand.Execute(null);
        await vm.History.HistoryLoad;

        Assert.Equal("⌕ TODO ≠*.md ▾", vm.History.SearchFilterLabel);
    }

    // ── Per-scope plumbing (what reaches git) ─────────────────────────────────────

    [Fact]
    public async Task Content_scope_passes_the_pickaxe_modifiers_to_git()
    {
        var (vm, git) = await LoadedWorkspace();

        await vm.History.UseContentSearchCommand.ExecuteAsync(null);
        vm.History.SearchUseRegex = true;            // -> --pickaxe-regex
        vm.History.ContentFilter = "Hello";          // case toggle off -> -i

        await vm.History.HistoryLoad;

        Assert.True(git.LastContentRegex);
        Assert.True(git.LastContentIgnoreCase);

        vm.History.SearchCaseSensitive = true;       // git-level modifier change reloads
        await vm.History.HistoryLoad;

        Assert.False(git.LastContentIgnoreCase);
    }

    [Fact]
    public async Task The_exclude_pathspec_only_applies_on_enter()
    {
        var (vm, git) = await LoadedWorkspace();

        await vm.History.UseFileSearchCommand.ExecuteAsync(null);
        vm.History.ExcludeText = "*.md";
        Assert.Null(git.LastPathExclude);              // typing alone applies nothing (git-level)

        vm.History.ApplyExcludeCommand.Execute(null);  // Enter
        await vm.History.HistoryLoad;

        Assert.Equal("*.md", git.LastPathExclude);
        Assert.False(vm.History.ShowGraph);
    }

    [Fact]
    public async Task The_path_boxes_only_appear_where_files_are_involved()
    {
        var (vm, _) = await LoadedWorkspace();

        // Message searches commit text — no file dimension, so it's just the query box.
        Assert.False(vm.History.ShowIncludeBox);
        Assert.False(vm.History.ShowExcludeBox);
        Assert.True(vm.History.CanUseRegex);

        // File: the query box IS the include path, leaving only an exclude box. git pathspec has no
        // regex form either, so .* is unavailable (Aa still maps to ":(icase)").
        await vm.History.UseFileSearchCommand.ExecuteAsync(null);
        Assert.False(vm.History.ShowIncludeBox);
        Assert.True(vm.History.ShowExcludeBox);
        Assert.False(vm.History.CanUseRegex);

        // Content searches inside files, so both path boxes narrow it — plus -i / --pickaxe-regex.
        await vm.History.UseContentSearchCommand.ExecuteAsync(null);
        Assert.True(vm.History.ShowIncludeBox);
        Assert.True(vm.History.ShowExcludeBox);
        Assert.True(vm.History.CanUseRegex);
    }

    [Fact]
    public async Task File_scope_folds_case_through_the_icase_pathspec()
    {
        var (vm, git) = await LoadedWorkspace();

        await vm.History.UseFileSearchCommand.ExecuteAsync(null);
        vm.History.SearchText = "README.md";
        vm.History.ApplySearchCommand.Execute(null);   // Enter
        await vm.History.HistoryLoad;

        Assert.True(git.LastPathIncludeIgnoreCase);    // Aa off -> ":(icase)"

        vm.History.SearchCaseSensitive = true;
        await vm.History.HistoryLoad;

        Assert.False(git.LastPathIncludeIgnoreCase);
    }

    // ── Real git: the ":(exclude)" pathspec and pickaxe modifiers ─────────────────

    [Fact]
    public async Task Git_level_path_exclude_drops_commits_that_only_touched_excluded_paths()
    {
        using var repo = new TestRepo();
        repo.WriteFile("app.cs", "1"); repo.Git("add", "-A"); repo.Git("commit", "-m", "code");
        repo.WriteFile("readme.md", "hi"); repo.Git("add", "-A"); repo.Git("commit", "-m", "docs");
        repo.WriteFile("app.cs", "2"); repo.Git("add", "-A"); repo.Git("commit", "-m", "more code");

        var git = new GitService();
        var commits = await git.GetCommitsAsync(repo.Path, pathExclude: "*.md");

        Assert.Equal(2, commits.Count);
        Assert.DoesNotContain(commits, c => c.Subject == "docs");
    }

    [Fact]
    public async Task Git_level_pickaxe_honours_regex_and_case_modifiers()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "plain"); repo.Git("add", "-A"); repo.Git("commit", "-m", "base");
        repo.WriteFile("a.txt", "plain\nHello World"); repo.Git("add", "-A"); repo.Git("commit", "-m", "adds hello");

        var git = new GitService();

        // Exact -S is case-sensitive: lowercase "hello" misses "Hello World"…
        var exact = await git.GetCommitsAsync(repo.Path, contentSearch: "hello");
        Assert.Empty(exact);

        // …-i finds it, and --pickaxe-regex matches a pattern.
        var insensitive = await git.GetCommitsAsync(repo.Path, contentSearch: "hello", contentIgnoreCase: true);
        Assert.Single(insensitive);
        Assert.Equal("adds hello", insensitive[0].Subject);

        var regex = await git.GetCommitsAsync(repo.Path, contentSearch: "Hel+o", contentRegex: true);
        Assert.Single(regex);
        Assert.Equal("adds hello", regex[0].Subject);
    }

    // ── Column clamp (the History table's narrow-pane fit) ────────────────────────

    [Fact]
    public void Columns_that_fit_come_back_untouched()
    {
        var (author, date, commit) = HistoryViewModel.ClampColumns(600, 92, 110, 66);

        Assert.Equal(92, author);
        Assert.Equal(110, date);
        Assert.Equal(66, commit);
    }

    [Fact]
    public void Overflowing_columns_shrink_proportionally_into_the_budget()
    {
        // budget = 400 - 80 (message min) = 320; 500 total → scale 0.64
        var (author, date, commit) = HistoryViewModel.ClampColumns(400, 200, 200, 100);

        Assert.Equal(128, author, 3);
        Assert.Equal(128, date, 3);
        Assert.Equal(64, commit, 3);
    }

    [Fact]
    public void Extreme_narrowness_floors_each_column_at_the_minimum()
    {
        var (author, date, commit) = HistoryViewModel.ClampColumns(100, 200, 200, 100);

        Assert.True(author >= HistoryViewModel.MinFixedColumnWidth);
        Assert.True(date >= HistoryViewModel.MinFixedColumnWidth);
        Assert.Equal(HistoryViewModel.MinFixedColumnWidth, commit);
    }
}
