using System.Threading.Tasks;
using GitFlick.Services;

namespace GitFlick.Tests;

/// <summary>
/// "Create branch from &lt;ref&gt;": the new branch must start at the given ref, not at HEAD.
/// </summary>
public class CreateBranchFromTests
{
    private readonly GitService _git = new();

    /// <summary>Repo with main@"first" plus a "base" branch that has one extra commit.</summary>
    private static TestRepo SeedRepo()
    {
        var repo = new TestRepo();
        repo.WriteFile("a.txt", "1");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");

        repo.Git("checkout", "-b", "base");
        repo.WriteFile("base.txt", "b");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "base work");

        repo.Git("checkout", "main");
        return repo;
    }

    [Fact]
    public async Task Branching_from_another_branch_starts_at_that_branch()
    {
        using var repo = SeedRepo();

        // Standing on main, branch off "base".
        var created = await _git.CreateBranchAsync(repo.Path, "feature", checkout: true, startPoint: "base");
        Assert.True(created.Succeeded, created.FailureMessage);

        var status = await _git.GetStatusAsync(repo.Path);
        Assert.Equal("feature", status.BranchName);

        // It started at "base", so base's commit is present — it wouldn't be if it forked from main.
        Assert.True(File.Exists(Path.Combine(repo.Path, "base.txt")));
    }

    [Fact]
    public async Task No_start_point_still_branches_from_head()
    {
        using var repo = SeedRepo();

        var created = await _git.CreateBranchAsync(repo.Path, "feature");
        Assert.True(created.Succeeded, created.FailureMessage);

        Assert.Equal("feature", (await _git.GetStatusAsync(repo.Path)).BranchName);
        Assert.False(File.Exists(Path.Combine(repo.Path, "base.txt")));   // forked from main
    }

    [Fact]
    public async Task A_blank_start_point_is_treated_as_head()
    {
        using var repo = SeedRepo();

        // The box is empty / whitespace — must not become a bogus git argument.
        var created = await _git.CreateBranchAsync(repo.Path, "feature", checkout: true, startPoint: "   ");
        Assert.True(created.Succeeded, created.FailureMessage);

        Assert.False(File.Exists(Path.Combine(repo.Path, "base.txt")));
    }
}
