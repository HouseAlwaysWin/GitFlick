using System.IO;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.Services;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>The Content search scope — git log -G, i.e. commits whose diff touches the text.</summary>
public class PickaxeSearchTests
{
    [Fact]
    public async Task Content_scope_applies_the_search_on_enter()
    {
        var git = new FakeGitService();
        var vm = new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));
        await vm.ShowHistoryCommand.ExecuteAsync(null);

        await vm.History.UseContentSearchCommand.ExecuteAsync(null);
        vm.History.SearchText = "needle";
        Assert.Null(git.LastContentSearch);          // typing doesn't reload — waits for apply

        vm.History.ApplySearchCommand.Execute(null);
        await vm.History.HistoryLoad;

        Assert.Equal("needle", git.LastContentSearch);
        Assert.True(vm.History.HasContentFilter);
        Assert.False(vm.History.ShowGraph);                  // a content subset isn't parent-closed
    }

    [Fact]
    public async Task Content_search_finds_every_commit_whose_diff_touches_the_text()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.cs", "int other = 0;\n"); repo.Git("add", "-A"); repo.Git("commit", "-m", "base");
        repo.WriteFile("a.cs", "int other = 0;\nint needle = 1;\n"); repo.Git("add", "-A"); repo.Git("commit", "-m", "add needle");
        repo.WriteFile("a.cs", "int other = 0;\nint needle = 2;\n"); repo.Git("add", "-A"); repo.Git("commit", "-m", "tweak needle");

        var git = new GitService();
        Assert.Equal(3, (await git.GetCommitsAsync(repo.Path)).Count);

        var found = await git.GetCommitsAsync(repo.Path, contentSearch: "needle");

        // -G matches any commit that added or removed a line containing the text. That includes the
        // "tweak" commit, which only edited the line — its occurrence count never changed, so the old
        // -S pickaxe missed it. The unrelated "base" commit doesn't match.
        Assert.Equal(2, found.Count);
        Assert.Contains(found, c => c.Subject.Contains("add needle"));
        Assert.Contains(found, c => c.Subject.Contains("tweak needle"));
    }

    [Fact]
    public async Task Content_search_matches_literally_unless_regex_is_on()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "value a.c\n"); repo.Git("add", "-A"); repo.Git("commit", "-m", "literal dot");
        repo.WriteFile("b.txt", "value axc\n"); repo.Git("add", "-A"); repo.Git("commit", "-m", "axc");

        var git = new GitService();

        // Regex off: "a.c" is escaped, so the "." is a literal dot — only the "a.c" line matches.
        var literal = await git.GetCommitsAsync(repo.Path, contentSearch: "a.c", contentRegex: false);
        Assert.Single(literal);
        Assert.Contains("literal dot", literal[0].Subject);

        // Regex on: "." is any character — both "a.c" and "axc" match.
        var regex = await git.GetCommitsAsync(repo.Path, contentSearch: "a.c", contentRegex: true);
        Assert.Equal(2, regex.Count);
    }
}
