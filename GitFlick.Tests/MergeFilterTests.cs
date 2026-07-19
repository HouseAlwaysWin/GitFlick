using System.IO;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.Services;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>
/// The "Merges only" toggle (<c>git log --merges</c>) and the file filter's <c>--full-history</c>,
/// which keeps merge commits that touched a file instead of letting git simplify them away.
/// </summary>
public class MergeFilterTests
{
    [Fact]
    public async Task Merges_only_reaches_git_and_hides_the_graph()
    {
        var git = new FakeGitService();
        var vm = new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));
        await vm.ShowHistoryCommand.ExecuteAsync(null);

        vm.MergesOnly = true;
        await vm.HistoryLoad;

        Assert.True(git.LastMergesOnly);
        Assert.False(vm.ShowGraph);   // merges-only isn't a parent-closed subset
    }

    [Fact]
    public async Task Merges_only_lists_only_the_merge_commit()
    {
        using var repo = new TestRepo();
        repo.WriteFile("f.txt", "1\n"); repo.Git("add", "-A"); repo.Git("commit", "-m", "c1 base");
        repo.Git("checkout", "-b", "feature");
        repo.WriteFile("g.txt", "x\n"); repo.Git("add", "-A"); repo.Git("commit", "-m", "feature work");
        repo.Git("checkout", "main");
        repo.WriteFile("f.txt", "2\n"); repo.Git("add", "-A"); repo.Git("commit", "-m", "c3 main");
        repo.Git("merge", "--no-ff", "feature", "-m", "merge feature");

        var vm = new WorkspaceViewModel(new GitService(), new RepositoryItem("r", repo.Path));
        await vm.ShowHistoryCommand.ExecuteAsync(null);
        vm.MergesOnly = true;
        await vm.HistoryLoad;

        var only = Assert.Single(vm.Commits);
        Assert.Equal("merge feature", only.Subject);
        Assert.True(only.IsMerge);
    }

    [Fact]
    public async Task File_filter_includes_a_merge_that_touched_the_file()
    {
        using var repo = new TestRepo();
        // f is changed only on the feature side; main touches an unrelated file. The merge brings
        // f=2 to main and is TREESAME to its feature parent, so git's default `-- f.txt` prunes it —
        // --full-history is what keeps it.
        repo.WriteFile("f.txt", "1\n"); repo.Git("add", "-A"); repo.Git("commit", "-m", "c1 base");
        repo.Git("checkout", "-b", "feature");
        repo.WriteFile("f.txt", "2\n"); repo.Git("add", "-A"); repo.Git("commit", "-m", "edit f on feature");
        repo.Git("checkout", "main");
        repo.WriteFile("a.txt", "y\n"); repo.Git("add", "-A"); repo.Git("commit", "-m", "unrelated on main");
        repo.Git("merge", "--no-ff", "feature", "-m", "merge feature");

        var vm = new WorkspaceViewModel(new GitService(), new RepositoryItem("r", repo.Path));
        await vm.ShowFileHistory("f.txt");
        await vm.HistoryLoad;

        Assert.Contains(vm.Commits, c => c.Subject == "merge feature" && c.IsMerge);
    }
}
