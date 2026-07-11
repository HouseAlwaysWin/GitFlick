using System.Linq;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.Services;

namespace GitFlick.Tests;

/// <summary>
/// End-to-end against the real git CLI. This is where spec §5③'s acceptance actually lives:
/// read a repo with CJK filenames without mojibake or octal escapes, and do a stage → commit.
/// </summary>
public class GitServiceTests
{
    private readonly GitService _git = new();

    [Fact]
    public async Task GetVersionAsync_finds_git_on_path()
    {
        var version = await _git.GetVersionAsync();

        Assert.NotNull(version);
        Assert.Contains("git version", version);
    }

    [Fact]
    public async Task GetVersionAsync_returns_null_for_a_bogus_executable()
    {
        var missing = new GitService(@"C:\does\not\exist\git.exe");
        Assert.Null(await missing.GetVersionAsync());
    }

    [Fact]
    public async Task IsRepositoryAsync_distinguishes_repos_from_plain_folders()
    {
        using var repo = new TestRepo();

        Assert.True(await _git.IsRepositoryAsync(repo.Path));
        Assert.False(await _git.IsRepositoryAsync(System.IO.Path.GetTempPath()));
    }

    [Fact]
    public async Task Reads_a_cjk_filename_without_mojibake_or_octal_escapes()
    {
        using var repo = new TestRepo();
        repo.WriteFile("測試檔案.txt", "內容");

        var status = await _git.GetStatusAsync(repo.Path);

        var entry = Assert.Single(status.Entries);
        Assert.Equal("測試檔案.txt", entry.Path);
        Assert.Equal(GitChangeKind.Untracked, entry.Kind);
        Assert.DoesNotContain("\\", entry.Path);   // e.g. no "\346\270\254"
    }

    [Fact]
    public async Task Stage_then_commit_a_cjk_file_end_to_end()
    {
        using var repo = new TestRepo();
        repo.WriteFile("報告.md", "# 標題");

        // Untracked -> unstaged.
        var before = await _git.GetStatusAsync(repo.Path);
        Assert.Contains(before.Unstaged, e => e.Path == "報告.md");
        Assert.Empty(before.Staged);

        // Stage it.
        var stage = await _git.StageAsync(repo.Path, "報告.md");
        Assert.True(stage.Succeeded, stage.FailureMessage);

        var staged = await _git.GetStatusAsync(repo.Path);
        Assert.Contains(staged.Staged, e => e.Path == "報告.md");

        // Commit with a Chinese message.
        var commit = await _git.CommitAsync(repo.Path, "新增報告檔案");
        Assert.True(commit.Succeeded, commit.FailureMessage);

        // Clean afterwards, and the message round-trips.
        var after = await _git.GetStatusAsync(repo.Path);
        Assert.True(after.IsClean);

        var log = repo.Git("log", "-1", "--pretty=%s").Trim();
        Assert.Equal("新增報告檔案", log);
    }

    [Fact]
    public async Task Unstage_moves_a_file_back_to_the_worktree()
    {
        using var repo = new TestRepo();
        repo.WriteFile("seed.txt", "seed");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "seed");

        repo.WriteFile("seed.txt", "changed");
        await _git.StageAsync(repo.Path, "seed.txt");
        Assert.Contains((await _git.GetStatusAsync(repo.Path)).Staged, e => e.Path == "seed.txt");

        var unstage = await _git.UnstageAsync(repo.Path, "seed.txt");
        Assert.True(unstage.Succeeded, unstage.FailureMessage);

        var status = await _git.GetStatusAsync(repo.Path);
        Assert.Contains(status.Unstaged, e => e.Path == "seed.txt");
        Assert.DoesNotContain(status.Staged, e => e.Path == "seed.txt");
    }

    [Fact]
    public async Task Status_reports_branch_and_ahead_behind_against_an_upstream()
    {
        using var origin = new TestRepo();
        origin.WriteFile("a.txt", "1");
        origin.Git("add", "-A");
        origin.Git("commit", "-m", "first");

        using var clone = new TestRepo();
        // Turn the fresh clone folder into an actual clone of origin.
        System.IO.Directory.Delete(clone.Path, recursive: true);
        new TestRepo().Git("clone", origin.Path, clone.Path);  // clone into the known path
        var git = new GitService();

        // Make origin advance by one commit, then fetch so the clone is "behind 1".
        origin.WriteFile("b.txt", "2");
        origin.Git("add", "-A");
        origin.Git("commit", "-m", "second");
        await git.FetchAsync(clone.Path);

        var status = await git.GetStatusAsync(clone.Path);
        Assert.False(status.IsDetached);
        Assert.Equal(1, status.Behind);
        Assert.Equal(0, status.Ahead);
    }

    [Fact]
    public async Task Lists_branches_with_the_current_one_marked()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "1");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");

        var create = await _git.CreateBranchAsync(repo.Path, "feature", checkout: false);
        Assert.True(create.Succeeded, create.FailureMessage);

        var branches = await _git.GetBranchesAsync(repo.Path);

        Assert.Contains(branches, b => b.Name == "main" && b.IsCurrent);
        Assert.Contains(branches, b => b.Name == "feature" && !b.IsCurrent);
    }

    [Fact]
    public async Task Checkout_and_create_switch_the_current_branch()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "1");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");

        Assert.True((await _git.CreateBranchAsync(repo.Path, "feature")).Succeeded);
        Assert.Equal("feature", (await _git.GetStatusAsync(repo.Path)).BranchName);

        Assert.True((await _git.CheckoutAsync(repo.Path, "main")).Succeeded);
        Assert.Equal("main", (await _git.GetStatusAsync(repo.Path)).BranchName);
    }

    [Fact]
    public async Task Merge_brings_in_a_branch()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "1");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");

        await _git.CreateBranchAsync(repo.Path, "feature");
        repo.WriteFile("feature.txt", "f");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "feature work");

        await _git.CheckoutAsync(repo.Path, "main");
        var merge = await _git.MergeAsync(repo.Path, "feature");

        Assert.True(merge.Succeeded, merge.FailureMessage);
        Assert.True(System.IO.File.Exists(System.IO.Path.Combine(repo.Path, "feature.txt")));
    }

    [Fact]
    public async Task Stash_push_list_and_pop_round_trip()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "1");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");

        repo.WriteFile("a.txt", "changed");
        var push = await _git.StashPushAsync(repo.Path, "wip changes");
        Assert.True(push.Succeeded, push.FailureMessage);
        Assert.True((await _git.GetStatusAsync(repo.Path)).IsClean);

        var stashes = await _git.GetStashesAsync(repo.Path);
        var stash = Assert.Single(stashes);
        Assert.Equal(0, stash.Index);
        Assert.Contains("wip changes", stash.Description);

        var pop = await _git.StashPopAsync(repo.Path);
        Assert.True(pop.Succeeded, pop.FailureMessage);
        Assert.Contains((await _git.GetStatusAsync(repo.Path)).Unstaged, e => e.Path == "a.txt");
    }
}
