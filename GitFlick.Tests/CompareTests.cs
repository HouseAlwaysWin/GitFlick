using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.Services;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>The compare-refs view: it lists only the compare-side commits/files and diffs one file.</summary>
public class CompareTests
{
    private static CommitInfo Commit(string sha, string subject = "s") =>
        new() { Sha = sha, Parents = [], Author = "a", When = default, Subject = subject };

    [Fact]
    public async Task Load_fills_commits_and_files_and_passes_both_refs()
    {
        var git = new FakeGitService();
        git.StubCompareCommits.Add(Commit("aaa1111"));
        git.StubCompareFiles.Add(new CommitFileEntry("src/x.cs", "M"));
        var vm = new CompareViewModel(git, Path.GetTempPath(), "main", "feature");

        await vm.LoadAsync();

        Assert.Equal("main", git.LastCompareBase);
        Assert.Equal("feature", git.LastCompareCompare);
        Assert.Single(vm.Commits);
        Assert.True(vm.HasCommits);
        Assert.Single(vm.Files);
    }

    [Fact]
    public async Task No_commits_leaves_HasCommits_false()
    {
        var git = new FakeGitService();
        var vm = new CompareViewModel(git, Path.GetTempPath(), "main", "feature");

        await vm.LoadAsync();

        Assert.False(vm.HasCommits);
        Assert.Empty(vm.Commits);
    }

    [Fact]
    public async Task Selecting_a_file_loads_its_range_diff()
    {
        var git = new FakeGitService();
        var vm = new CompareViewModel(git, Path.GetTempPath(), "main", "feature");
        await vm.LoadAsync();

        vm.SelectedFile = new CommitFileEntry("src/x.cs", "M");

        Assert.Equal("src/x.cs", vm.DiffPath);
        Assert.Equal("diff for src/x.cs", vm.DiffText);
    }

    [Fact]
    public void CreateCompare_wires_the_two_refs_through()
    {
        var git = new FakeGitService();
        var ws = new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));

        var compare = ws.CreateCompare("base", "compare");

        Assert.Equal("base", compare.BaseRef);
        Assert.Equal("compare", compare.CompareRef);
    }

    [Fact]
    public async Task Divergent_refs_show_only_the_compare_side()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "1\n");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "base");
        repo.Git("branch", "-M", "base");

        repo.Git("checkout", "-b", "feature");
        repo.WriteFile("a.txt", "2\n");
        repo.WriteFile("b.txt", "new\n");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "feature-work");

        var git = new GitService();

        var commits = await git.GetCommitsBetweenAsync(repo.Path, "base", "feature");
        Assert.Single(commits);
        Assert.Equal("feature-work", commits[0].Subject);

        var files = await git.GetDiffFilesAsync(repo.Path, "base", "feature");
        var paths = files.Select(f => f.Path).ToList();
        Assert.Contains("a.txt", paths);
        Assert.Contains("b.txt", paths);

        var diff = await git.GetRefRangeFileDiffAsync(repo.Path, "base", "feature", "a.txt");
        Assert.Contains("@@", diff);
        Assert.Contains("+2", diff);
    }
}
