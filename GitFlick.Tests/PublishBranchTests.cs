using System.Linq;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.Services;

namespace GitFlick.Tests;

/// <summary>
/// Pushing a branch the remote has never seen. Plain <c>git push</c> fails there ("no upstream"), so
/// the workspace offers to publish instead.
/// </summary>
public class PublishBranchTests
{
    private readonly GitService _git = new();

    [Fact]
    public async Task Publishing_puts_the_branch_on_the_remote_and_sets_tracking()
    {
        using var origin = new TestRepo();
        origin.Git("checkout", "-q", "-b", "main");
        using var repo = new TestRepo();
        repo.Git("remote", "add", "origin", origin.Path);
        repo.Git("checkout", "-b", "feature");
        repo.WriteFile("f.txt", "1");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "feature work");

        // Precondition: a plain push has nothing to push to.
        var plain = await _git.PushAsync(repo.Path);
        Assert.False(plain.Succeeded);
        Assert.Contains("upstream", plain.FailureMessage, System.StringComparison.OrdinalIgnoreCase);

        var published = await _git.PublishBranchAsync(repo.Path, "origin", "feature");
        Assert.True(published.Succeeded, published.FailureMessage);

        // Tracking is now set, so an ordinary push works from here on.
        var branch = (await _git.GetBranchesAsync(repo.Path)).Single(b => b.Name == "feature");
        Assert.Equal("origin/feature", branch.Upstream);
    }

    [Fact]
    public async Task A_branch_with_no_upstream_is_offered_for_publishing_rather_than_pushed()
    {
        var git = new FakeGitService();
        git.StubRemotes.Add("origin");
        git.StubStatus = new GitStatus { BranchName = "feature", Upstream = null, Ahead = 1 };

        var vm = WorkspaceFor(git);
        await vm.RefreshAsync();

        var asked = false;
        vm.ConfirmPublishBranch = (_, _) => { asked = true; return Task.FromResult(true); };

        await vm.PushCommand.ExecuteAsync(null);

        Assert.True(asked);
        Assert.Equal(("origin", "feature"), git.LastPublish);
    }

    [Fact]
    public async Task Declining_the_prompt_publishes_nothing()
    {
        var git = new FakeGitService();
        git.StubRemotes.Add("origin");
        git.StubStatus = new GitStatus { BranchName = "feature", Upstream = null, Ahead = 1 };

        var vm = WorkspaceFor(git);
        await vm.RefreshAsync();
        vm.ConfirmPublishBranch = (_, _) => Task.FromResult(false);

        await vm.PushCommand.ExecuteAsync(null);

        Assert.Null(git.LastPublish);
    }

    [Fact]
    public async Task A_branch_that_already_tracks_is_pushed_normally()
    {
        var git = new FakeGitService();
        git.StubRemotes.Add("origin");
        git.StubStatus = new GitStatus { BranchName = "feature", Upstream = "origin/feature", Ahead = 1 };

        var vm = WorkspaceFor(git);
        await vm.RefreshAsync();

        var asked = false;
        vm.ConfirmPublishBranch = (_, _) => { asked = true; return Task.FromResult(true); };

        await vm.PushCommand.ExecuteAsync(null);

        Assert.False(asked);            // nothing to publish — it already tracks
        Assert.Null(git.LastPublish);
    }

    private static GitFlick.ViewModels.WorkspaceViewModel WorkspaceFor(FakeGitService git) =>
        new(git, new RepositoryItem("r", System.IO.Path.GetTempPath()));
}
