using System.IO;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.Services;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>The Content search scope (git log -S pickaxe).</summary>
public class PickaxeSearchTests
{
    [Fact]
    public async Task Content_scope_applies_a_pickaxe_search_on_enter()
    {
        var git = new FakeGitService();
        var vm = new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));
        await vm.ShowHistoryCommand.ExecuteAsync(null);

        await vm.UseContentSearchCommand.ExecuteAsync(null);
        vm.SearchText = "needle";
        Assert.Null(git.LastContentSearch);          // typing doesn't reload — waits for apply

        vm.ApplySearchCommand.Execute(null);
        await vm.HistoryLoad;

        Assert.Equal("needle", git.LastContentSearch);
        Assert.True(vm.HasContentFilter);
        Assert.False(vm.ShowGraph);                  // a pickaxe subset isn't parent-closed
    }

    [Fact]
    public async Task Pickaxe_finds_commits_that_changed_the_strings_occurrences()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.cs", "hello world"); repo.Git("add", "-A"); repo.Git("commit", "-m", "c1");
        repo.WriteFile("a.cs", "hello NEEDLE world"); repo.Git("add", "-A"); repo.Git("commit", "-m", "add needle");
        repo.WriteFile("a.cs", "hello world"); repo.Git("add", "-A"); repo.Git("commit", "-m", "remove needle");

        var git = new GitService();
        Assert.Equal(3, (await git.GetCommitsAsync(repo.Path)).Count);

        var found = await git.GetCommitsAsync(repo.Path, contentSearch: "NEEDLE");

        // -S reports commits where the count changed: the add (0→1) and the remove (1→0).
        Assert.Equal(2, found.Count);
        Assert.All(found, c => Assert.Contains("needle", c.Subject));
    }
}
