using System.Threading.Tasks;
using GitFlick.Services;

namespace GitFlick.Tests;

/// <summary>
/// The combined ("--cc") view of a merge: the lines that match neither parent, i.e. what a human
/// decided while resolving. This content exists in no other commit, so a plain log never shows it.
/// </summary>
public class MergeResolutionTests
{
    private readonly GitService _git = new();

    /// <summary>main and "other" both edit the same line, then merge with a hand-written resolution.</summary>
    private static TestRepo RepoWithResolvedConflict()
    {
        var repo = new TestRepo();
        repo.WriteFile("a.txt", "base\n");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");

        repo.Git("checkout", "-b", "other");
        repo.WriteFile("a.txt", "from-other\n");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "other side");

        repo.Git("checkout", "main");
        repo.WriteFile("a.txt", "from-main\n");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "main side");

        // Conflicts; resolve to something that is NEITHER side, which is what makes it show up in --cc.
        repo.GitAllowFail("merge", "other");   // conflicts, exits 1, leaves markers in a.txt
        repo.WriteFile("a.txt", "hand-resolved\n");
        repo.Git("add", "-A");
        repo.Git("commit", "--no-edit");
        return repo;
    }

    [Fact]
    public async Task A_hand_resolved_merge_lists_the_file_and_shows_the_decision()
    {
        using var repo = RepoWithResolvedConflict();
        var head = (await _git.GetCommitsAsync(repo.Path, 5))[0];
        Assert.True(head.IsMerge);

        var files = await _git.GetMergeResolutionFilesAsync(repo.Path, head.Sha);
        Assert.Single(files);
        Assert.Equal("a.txt", files[0].Path);

        var patch = await _git.GetMergeResolutionFileDiffAsync(repo.Path, head.Sha, "a.txt");

        // The resolution matched neither side, so the combined diff carries the line that won.
        Assert.Contains("hand-resolved", patch);
    }

    [Fact]
    public async Task A_merge_git_resolved_on_its_own_has_nothing_to_show()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "base\n");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");

        // Touch different files, so the merge is automatic — no human decision to record.
        repo.Git("checkout", "-b", "other");
        repo.WriteFile("b.txt", "b\n");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "other side");

        repo.Git("checkout", "main");
        repo.WriteFile("c.txt", "c\n");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "main side");
        repo.Git("merge", "other", "--no-edit");

        var head = (await _git.GetCommitsAsync(repo.Path, 5))[0];
        Assert.True(head.IsMerge);

        Assert.Empty(await _git.GetMergeResolutionFilesAsync(repo.Path, head.Sha));
    }

    [Fact]
    public async Task An_ordinary_commit_has_no_resolution()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "1\n");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");

        var head = (await _git.GetCommitsAsync(repo.Path, 5))[0];
        Assert.False(head.IsMerge);
        Assert.Empty(await _git.GetMergeResolutionFilesAsync(repo.Path, head.Sha));
    }
}
