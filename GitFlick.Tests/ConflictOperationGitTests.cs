using System.IO;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.Services;

namespace GitFlick.Tests;

/// <summary>
/// The conflict plumbing against the real git CLI: detecting which operation is paused, resolving a
/// path to either side, accepting a deletion, and finishing / abandoning the operation. Real conflicts
/// are set up with <see cref="TestRepo.GitAllowFail"/> (a conflicting merge/rebase exits non-zero by
/// design).
/// </summary>
public class ConflictOperationGitTests
{
    private readonly GitService _git = new();

    /// <summary>base → branch changes a line to "theirs", main changes it to "ours", then main merges.</summary>
    private static TestRepo ConflictingMerge()
    {
        var repo = new TestRepo();
        repo.WriteFile("f.txt", "base\n");
        repo.Git("add", "-A"); repo.Git("commit", "-m", "base");

        repo.Git("checkout", "-b", "feature");
        repo.WriteFile("f.txt", "theirs\n");
        repo.Git("add", "-A"); repo.Git("commit", "-m", "feature edit");

        repo.Git("checkout", "main");
        repo.WriteFile("f.txt", "ours\n");
        repo.Git("add", "-A"); repo.Git("commit", "-m", "main edit");

        repo.GitAllowFail("merge", "feature");   // conflicts on f.txt
        return repo;
    }

    [Fact]
    public async Task A_conflicting_merge_is_detected_and_can_be_aborted()
    {
        using var repo = ConflictingMerge();

        Assert.Equal(ConflictOperation.Merge, await _git.GetConflictOperationAsync(repo.Path));

        Assert.True((await _git.AbortOperationAsync(repo.Path, ConflictOperation.Merge)).Succeeded);

        Assert.Equal(ConflictOperation.None, await _git.GetConflictOperationAsync(repo.Path));
        Assert.Equal("ours\n", File.ReadAllText(Path.Combine(repo.Path, "f.txt")));   // pre-merge state restored
    }

    [Fact]
    public async Task Take_ours_and_take_theirs_write_the_right_side_without_markers()
    {
        using var repo = ConflictingMerge();

        await _git.TakeOursAsync(repo.Path, "f.txt");
        var ours = File.ReadAllText(Path.Combine(repo.Path, "f.txt"));
        Assert.Equal("ours\n", ours);
        Assert.DoesNotContain("<<<<<<<", ours);

        await _git.TakeTheirsAsync(repo.Path, "f.txt");
        Assert.Equal("theirs\n", File.ReadAllText(Path.Combine(repo.Path, "f.txt")));
    }

    [Fact]
    public async Task Resolving_then_continuing_produces_a_two_parent_merge_commit()
    {
        using var repo = ConflictingMerge();

        await _git.TakeOursAsync(repo.Path, "f.txt");
        await _git.StageAsync(repo.Path, "f.txt");                       // mark resolved

        Assert.True((await _git.ContinueOperationAsync(repo.Path, ConflictOperation.Merge)).Succeeded);

        Assert.Equal(ConflictOperation.None, await _git.GetConflictOperationAsync(repo.Path));
        // The merge landed as a merge commit: HEAD has two parents.
        var parents = repo.Git("rev-list", "--parents", "-n", "1", "HEAD").Trim().Split(' ');
        Assert.Equal(3, parents.Length);   // <commit> <parent1> <parent2>
    }

    [Fact]
    public async Task A_modify_delete_conflict_can_accept_the_deletion()
    {
        var repo = new TestRepo();
        repo.WriteFile("gone.txt", "content\n");
        repo.Git("add", "-A"); repo.Git("commit", "-m", "base");

        repo.Git("checkout", "-b", "edit");
        repo.WriteFile("gone.txt", "edited\n");
        repo.Git("add", "-A"); repo.Git("commit", "-m", "edit it");

        repo.Git("checkout", "main");
        repo.Git("rm", "gone.txt"); repo.Git("commit", "-m", "delete it");

        using (repo)
        {
            repo.GitAllowFail("merge", "edit");   // modify/delete conflict

            Assert.Equal(ConflictOperation.Merge, await _git.GetConflictOperationAsync(repo.Path));

            // Accept the deletion, then the merge completes cleanly.
            Assert.True((await _git.RemoveFileAsync(repo.Path, "gone.txt")).Succeeded);
            Assert.True((await _git.ContinueOperationAsync(repo.Path, ConflictOperation.Merge)).Succeeded);

            Assert.False(File.Exists(Path.Combine(repo.Path, "gone.txt")));
            Assert.Equal(ConflictOperation.None, await _git.GetConflictOperationAsync(repo.Path));
        }
    }

    [Fact]
    public async Task A_conflicting_rebase_is_detected_as_rebase_and_can_be_aborted()
    {
        var repo = new TestRepo();
        repo.WriteFile("f.txt", "base\n");
        repo.Git("add", "-A"); repo.Git("commit", "-m", "base");

        repo.Git("checkout", "-b", "topic");
        repo.WriteFile("f.txt", "topic\n");
        repo.Git("add", "-A"); repo.Git("commit", "-m", "topic edit");

        repo.Git("checkout", "main");
        repo.WriteFile("f.txt", "main\n");
        repo.Git("add", "-A"); repo.Git("commit", "-m", "main edit");

        using (repo)
        {
            repo.Git("checkout", "topic");
            repo.GitAllowFail("rebase", "main");   // conflicts replaying topic onto main

            // Rebase is a state directory, not a pseudo-ref — this is the path that would break if we
            // only checked MERGE_HEAD.
            Assert.Equal(ConflictOperation.Rebase, await _git.GetConflictOperationAsync(repo.Path));

            Assert.True((await _git.AbortOperationAsync(repo.Path, ConflictOperation.Rebase)).Succeeded);
            Assert.Equal(ConflictOperation.None, await _git.GetConflictOperationAsync(repo.Path));
        }
    }

    [Fact]
    public async Task A_conflicting_cherry_pick_is_detected_as_cherry_pick()
    {
        var repo = new TestRepo();
        repo.WriteFile("f.txt", "base\n");
        repo.Git("add", "-A"); repo.Git("commit", "-m", "base");

        repo.Git("checkout", "-b", "side");
        repo.WriteFile("f.txt", "side\n");
        repo.Git("add", "-A"); repo.Git("commit", "-m", "side edit");
        var sideSha = repo.Git("rev-parse", "HEAD").Trim();

        repo.Git("checkout", "main");
        repo.WriteFile("f.txt", "main\n");
        repo.Git("add", "-A"); repo.Git("commit", "-m", "main edit");

        using (repo)
        {
            repo.GitAllowFail("cherry-pick", sideSha);   // conflicts

            Assert.Equal(ConflictOperation.CherryPick, await _git.GetConflictOperationAsync(repo.Path));

            await _git.AbortOperationAsync(repo.Path, ConflictOperation.CherryPick);
            Assert.Equal(ConflictOperation.None, await _git.GetConflictOperationAsync(repo.Path));
        }
    }

    [Fact]
    public async Task A_conflicting_revert_is_detected_as_revert()
    {
        var repo = new TestRepo();
        repo.WriteFile("f.txt", "one\n");
        repo.Git("add", "-A"); repo.Git("commit", "-m", "first");
        var firstSha = repo.Git("rev-parse", "HEAD").Trim();

        repo.WriteFile("f.txt", "two\n");
        repo.Git("add", "-A"); repo.Git("commit", "-m", "second changes same line");

        using (repo)
        {
            repo.GitAllowFail("revert", "--no-edit", firstSha);   // reverting the first edit conflicts with the second

            Assert.Equal(ConflictOperation.Revert, await _git.GetConflictOperationAsync(repo.Path));

            await _git.AbortOperationAsync(repo.Path, ConflictOperation.Revert);
            Assert.Equal(ConflictOperation.None, await _git.GetConflictOperationAsync(repo.Path));
        }
    }

    [Fact]
    public async Task No_operation_in_a_clean_repo()
    {
        using var repo = new TestRepo();
        repo.WriteFile("f.txt", "x"); repo.Git("add", "-A"); repo.Git("commit", "-m", "c");

        Assert.Equal(ConflictOperation.None, await _git.GetConflictOperationAsync(repo.Path));
    }
}
