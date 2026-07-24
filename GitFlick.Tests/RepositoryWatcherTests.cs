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

        await Task.Delay(1100);                  // clear the "we just refreshed" window
        var statusBefore = git.StatusCallCount;

        await vm.RefreshFromDiskAsync();

        Assert.True(git.StatusCallCount > statusBefore);   // the view reloaded…
        Assert.Equal(0, git.FetchCount);                   // …without a network round-trip
    }

    [Fact]
    public async Task Our_own_command_churn_does_not_cause_a_second_reload()
    {
        var git = new FakeGitService();
        var vm = new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));

        await vm.RefreshAsync();                 // stands in for a command finishing
        var statusBefore = git.StatusCallCount;

        await vm.RefreshFromDiskAsync();         // the watcher seeing that same churn

        Assert.Equal(statusBefore, git.StatusCallCount);
    }

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
