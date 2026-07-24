using System;
using System.IO;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>
/// The one-shot "Clear filters" button: dropping half a dozen filters shouldn't mean clearing each
/// dropdown in turn — nor should it mean one `git log` per filter on the way out.
/// </summary>
public class HistoryClearFiltersTests
{
    private static string Sha(int i) => i.ToString("x").PadLeft(40, '0');

    private static async Task<(WorkspaceViewModel Vm, FakeGitService Git)> LoadedWorkspace()
    {
        var git = new FakeGitService();
        for (var i = 0; i < 3; i++)
        {
            git.StubCommits.Add(new CommitInfo
            {
                Sha = Sha(i),
                Parents = Array.Empty<string>(),
                Author = i == 0 ? "Alice" : "Bob",
                When = DateTimeOffset.FromUnixTimeSeconds(2_000_000 - i),
                Subject = "commit " + i,
            });
        }

        var vm = new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));
        await vm.ShowHistoryCommand.ExecuteAsync(null);
        return (vm, git);
    }

    /// <summary>Turns on one of everything — the state the button exists to undo.</summary>
    private static async Task ApplyEveryFilter(WorkspaceViewModel vm)
    {
        vm.History.MessageFilter = "commit";
        vm.History.SearchUseRegex = true;
        vm.History.SearchCaseSensitive = true;
        vm.History.FileFilter = "src/";
        vm.History.FileExcludeFilter = "*.md";
        vm.History.ContentFilter = "TODO";
        vm.History.SinceDate = new DateTime(2026, 7, 1);
        vm.History.UntilDate = new DateTime(2026, 7, 17);
        vm.History.FirstParentOnly = true;
        vm.History.MergesOnly = true;
        vm.History.SortColumn = HistorySortColumn.Author;
        vm.History.SortDescending = true;

        foreach (var author in vm.History.AuthorFilters)
        {
            author.IsSelected = true;
        }

        await vm.History.HistoryLoad;
    }

    [Fact]
    public async Task The_button_only_shows_when_something_is_filtered()
    {
        var (vm, _) = await LoadedWorkspace();
        Assert.False(vm.History.HasAnyFilter);

        vm.History.MessageFilter = "commit";
        Assert.True(vm.History.HasAnyFilter);

        vm.History.ClearAllFiltersCommand.Execute(null);
        await vm.History.HistoryLoad;
        Assert.False(vm.History.HasAnyFilter);
    }

    [Theory]
    [InlineData("author")]
    [InlineData("date")]
    [InlineData("firstParent")]
    [InlineData("merges")]
    [InlineData("sort")]
    [InlineData("exclude")]
    public async Task Any_single_filter_reveals_the_button(string which)
    {
        var (vm, _) = await LoadedWorkspace();
        Assert.False(vm.History.HasAnyFilter);

        switch (which)
        {
            case "author": vm.History.AuthorFilters[0].IsSelected = true; break;
            case "date": vm.History.SinceDate = new DateTime(2026, 7, 1); break;
            case "firstParent": vm.History.FirstParentOnly = true; break;
            case "merges": vm.History.MergesOnly = true; break;
            case "sort": vm.History.SortColumn = HistorySortColumn.Date; break;
            case "exclude": vm.History.FileExcludeFilter = "*.md"; break;
        }

        await vm.History.HistoryLoad;
        Assert.True(vm.History.HasAnyFilter);
    }

    [Fact]
    public async Task Clearing_drops_every_filter_at_once()
    {
        var (vm, _) = await LoadedWorkspace();
        await ApplyEveryFilter(vm);
        Assert.True(vm.History.HasAnyFilter);

        vm.History.ClearAllFiltersCommand.Execute(null);
        await vm.History.HistoryLoad;

        Assert.False(vm.History.HasAnyFilter);
        Assert.Empty(vm.History.MessageFilter);
        Assert.Empty(vm.History.FileFilter);
        Assert.Empty(vm.History.ContentFilter);
        Assert.Empty(vm.History.FileExcludeFilter);
        Assert.Empty(vm.History.SearchText);
        Assert.Empty(vm.History.IncludeText);
        Assert.Empty(vm.History.ExcludeText);
        Assert.Null(vm.History.SinceDate);
        Assert.Null(vm.History.UntilDate);
        Assert.False(vm.History.FirstParentOnly);
        Assert.False(vm.History.MergesOnly);
        Assert.False(vm.History.SearchUseRegex);
        Assert.False(vm.History.SearchCaseSensitive);
        Assert.Equal(HistorySortColumn.Graph, vm.History.SortColumn);
        Assert.False(vm.History.SortDescending);
        Assert.True(vm.History.IsMessageSearch);
        Assert.DoesNotContain(vm.History.AuthorFilters, a => a.IsSelected);

        // Everything back on screen, graph included.
        Assert.Equal(3, vm.History.Commits.Count);
        Assert.True(vm.History.ShowGraph);
    }

    [Fact]
    public async Task Clearing_reloads_the_log_exactly_once()
    {
        var (vm, git) = await LoadedWorkspace();
        await ApplyEveryFilter(vm);

        // Six of those filters are git-level; clearing them one by one would be six `git log` runs.
        var before = git.CommitsCallCount;

        vm.History.ClearAllFiltersCommand.Execute(null);
        await vm.History.HistoryLoad;

        Assert.Equal(1, git.CommitsCallCount - before);
    }

    [Fact]
    public async Task The_cleared_reload_asks_git_for_an_unfiltered_log()
    {
        var (vm, git) = await LoadedWorkspace();
        await ApplyEveryFilter(vm);

        vm.History.ClearAllFiltersCommand.Execute(null);
        await vm.History.HistoryLoad;

        Assert.Null(git.LastPathFilter);
        Assert.Null(git.LastPathExclude);
        Assert.Null(git.LastContentSearch);
        Assert.Null(git.LastSince);
        Assert.Null(git.LastUntil);
        Assert.False(git.LastFirstParentOnly);
        Assert.False(git.LastMergesOnly);
    }
}
