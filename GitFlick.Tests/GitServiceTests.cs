using System.Collections.Generic;
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
    public async Task Diff_shows_unstaged_changes_with_added_and_removed_lines()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "one\ntwo\n");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");

        repo.WriteFile("a.txt", "one\nTWO\n");

        var diff = await _git.GetDiffAsync(repo.Path, "a.txt", staged: false);

        Assert.Contains("-two", diff);
        Assert.Contains("+TWO", diff);
        Assert.Contains("@@", diff);
    }

    [Fact]
    public async Task Staged_and_unstaged_diffs_are_different_views()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "base\n");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");

        repo.WriteFile("a.txt", "staged\n");
        repo.Git("add", "a.txt");        // "staged" is now in the index
        repo.WriteFile("a.txt", "worktree\n");  // and the worktree moved on again

        var staged = await _git.GetDiffAsync(repo.Path, "a.txt", staged: true);
        var unstaged = await _git.GetDiffAsync(repo.Path, "a.txt", staged: false);

        Assert.Contains("+staged", staged);       // index vs HEAD
        Assert.Contains("-staged", unstaged);     // worktree vs index
        Assert.Contains("+worktree", unstaged);
    }

    [Fact]
    public async Task Diff_of_an_untracked_file_shows_it_as_all_added()
    {
        using var repo = new TestRepo();
        repo.WriteFile("seed.txt", "s");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "seed");

        // Untracked: plain `git diff` prints nothing, so this must go through --no-index.
        repo.WriteFile("brand-new.txt", "hello\nworld\n");

        var plain = await _git.GetDiffAsync(repo.Path, "brand-new.txt", staged: false);
        Assert.Empty(plain);   // proves the untracked flag is actually needed

        var diff = await _git.GetDiffAsync(repo.Path, "brand-new.txt", staged: false, untracked: true);

        Assert.Contains("+hello", diff);
        Assert.Contains("+world", diff);
    }

    [Fact]
    public async Task Diff_keeps_cjk_content_and_paths_readable()
    {
        using var repo = new TestRepo();
        repo.WriteFile("報告.txt", "第一行\n");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");

        repo.WriteFile("報告.txt", "第一行\n第二行\n");

        var diff = await _git.GetDiffAsync(repo.Path, "報告.txt", staged: false);

        Assert.Contains("+第二行", diff);
        Assert.Contains("報告.txt", diff);
        Assert.DoesNotContain("\\346", diff);   // no octal escaping
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
    public async Task Remote_branches_exclude_the_origin_HEAD_symref()
    {
        using var origin = new TestRepo();
        origin.WriteFile("a.txt", "1");
        origin.Git("add", "-A");
        origin.Git("commit", "-m", "first");

        using var clone = new TestRepo();
        System.IO.Directory.Delete(clone.Path, recursive: true);
        // A clone creates refs/remotes/origin/main AND the symbolic refs/remotes/origin/HEAD.
        new TestRepo().Git("clone", origin.Path, clone.Path);

        var remotes = await _git.GetRemoteBranchesAsync(clone.Path);

        Assert.Contains("origin/main", remotes);
        // The HEAD symref shortens to a bare "origin"; it must not surface as a branch.
        Assert.DoesNotContain("origin", remotes);
        Assert.DoesNotContain(remotes, r => r.EndsWith("/HEAD", System.StringComparison.Ordinal));
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

    /// <summary>Builds: main(3 commits) with a feature branch merged back in via --no-ff.</summary>
    private static TestRepo MergeTopology()
    {
        var repo = new TestRepo();

        repo.WriteFile("a.txt", "1");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first on main");

        repo.Git("checkout", "-b", "feature");
        repo.WriteFile("f.txt", "f");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "feature work");

        repo.Git("checkout", "main");
        repo.WriteFile("a.txt", "2");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "second on main");

        repo.Git("merge", "--no-ff", "-m", "Merge feature", "feature");
        repo.Git("tag", "v1.0");

        return repo;
    }

    [Fact]
    public async Task Commits_carry_parents_refs_and_head()
    {
        using var repo = MergeTopology();

        var commits = await _git.GetCommitsAsync(repo.Path);

        var merge = commits.First(c => c.Subject == "Merge feature");
        Assert.True(merge.IsMerge);
        Assert.Equal(2, merge.Parents.Count);   // a merge must report BOTH parents, or no lanes
        Assert.True(merge.IsHead);
        Assert.Contains(merge.Refs, r => r.Name == "main" && r.Kind == GitRefKind.LocalBranch);
        Assert.Contains(merge.Refs, r => r.Name == "v1.0" && r.Kind == GitRefKind.Tag);

        Assert.Contains(commits, c => c.Subject == "feature work");
        Assert.Contains(commits, c => c.Refs.Any(r => r.Name == "feature"));

        // The root has no parents.
        Assert.Empty(commits.Single(c => c.Subject == "first on main").Parents);
    }

    [Fact]
    public async Task Commits_are_ordered_so_a_parent_never_precedes_its_child()
    {
        // The lane algorithm depends on this. --date-order guarantees it; plain log ordering
        // does not, and a violation makes lanes wait forever for a SHA that already went by.
        using var repo = MergeTopology();

        var commits = await _git.GetCommitsAsync(repo.Path);
        var seen = new HashSet<string>();

        foreach (var commit in commits)
        {
            foreach (var parent in commit.Parents)
            {
                Assert.False(seen.Contains(parent), $"parent {parent[..7]} came before its child");
            }

            seen.Add(commit.Sha);
        }
    }

    [Fact]
    public async Task First_parent_only_drops_the_merged_branch_commits()
    {
        using var repo = MergeTopology();

        var full = await _git.GetCommitsAsync(repo.Path);
        var collapsed = await _git.GetCommitsAsync(repo.Path, firstParentOnly: true);

        Assert.Contains(full, c => c.Subject == "feature work");
        Assert.DoesNotContain(collapsed, c => c.Subject == "feature work");

        // The merge stays: one row standing for the whole branch that landed.
        Assert.Contains(collapsed, c => c.Subject == "Merge feature");
        Assert.True(collapsed.Count < full.Count);
    }

    [Fact]
    public async Task Commit_diff_shows_what_that_commit_changed()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "one\n");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "first");

        repo.WriteFile("a.txt", "one\n二行\n");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "second");

        var head = (await _git.GetCommitsAsync(repo.Path)).First();
        var diff = await _git.GetCommitDiffAsync(repo.Path, head.Sha);

        Assert.Contains("+二行", diff);   // CJK survives the commit diff too
        Assert.Contains("a.txt", diff);
    }

    [Fact]
    public async Task A_repo_with_no_commits_yields_an_empty_history_rather_than_an_error()
    {
        using var repo = new TestRepo();   // freshly init'd, nothing committed

        Assert.Empty(await _git.GetCommitsAsync(repo.Path));
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
