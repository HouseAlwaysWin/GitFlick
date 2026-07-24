using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.Services;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>
/// Noticing work done outside GitFlick. The remote-oriented auto-sync can't cover this: staging and
/// local edits never move ahead/behind/upstream, so only watching the repo's own files catches them.
/// </summary>
public class RepositoryWatcherTests
{
    [Theory]
    // Working-tree paths always matter.
    [InlineData(@"C:\repo\src\App.cs", false)]
    [InlineData(@"C:\repo\README.md", false)]
    // The bits of .git that say the view is stale.
    [InlineData(@"C:\repo\.git\HEAD", false)]
    [InlineData(@"C:\repo\.git\index", false)]
    [InlineData(@"C:\repo\.git\refs\heads\master", false)]
    // Pure churn.
    [InlineData(@"C:\repo\.git\index.lock", true)]
    [InlineData(@"C:\repo\.git\refs\heads\master.lock", true)]
    [InlineData(@"C:\repo\.git\objects\ab\cdef123", true)]
    [InlineData(@"C:\repo\.git\FETCH_HEAD", true)]
    public void Noise_is_told_apart_from_real_change(string path, bool expected) =>
        Assert.Equal(expected, RepositoryWatcher.IsNoise(path));

    [Fact]
    public void A_working_tree_objects_folder_is_not_mistaken_for_gits()
    {
        // Only ".git/objects" is churn — a project that happens to have an "objects" folder is content.
        Assert.False(RepositoryWatcher.IsNoise(@"C:\repo\objects\model.cs"));
        Assert.False(RepositoryWatcher.IsNoise(@"C:\repo\src\objects\thing.cs"));
    }

    [Fact]
    public async Task A_burst_of_edits_is_reported_once()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gitflick-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            using var watcher = new RepositoryWatcher();
            var fired = 0;
            watcher.Changed += (_, _) => Interlocked.Increment(ref fired);
            watcher.Watch(dir);

            // A single git command touches many paths in quick succession; that must read as one change.
            for (var i = 0; i < 12; i++)
            {
                File.WriteAllText(Path.Combine(dir, $"file{i}.txt"), "x");
                await Task.Delay(15);
            }

            await WaitFor(() => Volatile.Read(ref fired) > 0, TimeSpan.FromSeconds(5));
            await Task.Delay(400);   // let any straggler debounce land

            Assert.Equal(1, Volatile.Read(ref fired));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Stopping_the_watcher_silences_it()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gitflick-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            using var watcher = new RepositoryWatcher();
            var fired = 0;
            watcher.Changed += (_, _) => Interlocked.Increment(ref fired);

            watcher.Watch(dir);
            watcher.Stop();

            File.WriteAllText(Path.Combine(dir, "after-stop.txt"), "x");
            await Task.Delay(1200);

            Assert.Equal(0, Volatile.Read(ref fired));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Watching_a_missing_path_is_harmless()
    {
        using var watcher = new RepositoryWatcher();

        // Closing a repo (or a path that vanished) must not throw — it just stops watching.
        watcher.Watch(null);
        watcher.Watch(Path.Combine(Path.GetTempPath(), "gitflick-does-not-exist-" + Guid.NewGuid().ToString("N")));
    }

    // ── The view-model guard ─────────────────────────────────────────────────────

    [Fact]
    public async Task An_external_change_reloads_without_fetching()
    {
        var git = new FakeGitService();
        git.StubRemotes.Add("origin");
        var vm = new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));
        await vm.RefreshAsync();

        // Something actually changed on disk since that refresh.
        git.StubStatus = new GitStatus { BranchName = "main", Entries = [Modified("a.txt")] };
        var statusBefore = git.StatusCallCount;

        await vm.RefreshFromDiskAsync();

        Assert.True(git.StatusCallCount > statusBefore);   // the view reloaded…
        Assert.Equal(0, git.FetchCount);                   // …without a network round-trip
        Assert.Single(vm.UnstagedFiles);
    }

    [Fact]
    public async Task Churn_that_changes_nothing_costs_one_status_read_and_no_reload()
    {
        var git = new FakeGitService();
        var vm = new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));

        await vm.RefreshAsync();
        var statusBefore = git.StatusCallCount;

        // Build output landing in bin/obj, or git rewriting its own index: the watcher can't help
        // seeing it, but the repo state is identical, so it must not rebuild anything.
        await vm.RefreshFromDiskAsync();

        Assert.Equal(1, git.StatusCallCount - statusBefore);   // the compare, and nothing more
    }

    [Fact]
    public async Task A_background_reload_keeps_the_commit_the_user_has_open()
    {
        var git = new FakeGitService();
        git.StubCommits.Add(new CommitInfo
        {
            Sha = "a".PadLeft(40, '0'),
            Parents = System.Array.Empty<string>(),
            Author = "Dev",
            When = System.DateTimeOffset.UnixEpoch,
            Subject = "the commit being read",
        });

        var vm = new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));
        await vm.ShowHistoryCommand.ExecuteAsync(null);
        vm.History.SelectedCommit = vm.History.Commits[0];

        // A background refresh has no business closing the pane the user is reading.
        git.StubStatus = new GitStatus { BranchName = "main", Entries = [Modified("a.txt")] };
        await vm.RefreshFromDiskAsync();

        Assert.NotNull(vm.History.SelectedCommit);
        Assert.Equal("the commit being read", vm.History.SelectedCommit!.Subject);
    }

    private static GitStatusEntry Modified(string path) => new()
    {
        Path = path,
        Kind = GitChangeKind.Ordinary,
        UnstagedState = GitFileState.Modified,
    };

    [Fact]
    public async Task It_stands_down_while_a_command_is_running()
    {
        var git = new FakeGitService();
        var vm = new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath())) { IsBusy = true };
        var statusBefore = git.StatusCallCount;

        await vm.RefreshFromDiskAsync();

        Assert.Equal(statusBefore, git.StatusCallCount);
    }

    private static async Task WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(50);
        }
    }
}
