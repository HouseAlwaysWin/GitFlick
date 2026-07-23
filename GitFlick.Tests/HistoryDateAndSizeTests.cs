using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.Services;
using GitFlick.Services.Updates;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>
/// History's date-range filter (git --since/--until), the "keep paging until a client-side filter yields
/// more" Load-more fix, and the configurable page size.
/// </summary>
public class HistoryDateAndSizeTests
{
    private static RepositoryItem Item() => new("r", Path.GetTempPath());

    private static string Sha(int i) => i.ToString("x").PadLeft(40, '0');

    private static CommitInfo Commit(int i, int total, string? subject = null) => new()
    {
        Sha = Sha(i),
        Parents = i < total - 1 ? new[] { Sha(i + 1) } : Array.Empty<string>(),
        Author = "Dev",
        When = DateTimeOffset.FromUnixTimeSeconds(2_000_000 - i),   // newest first, like git log
        Subject = subject ?? ("commit " + i),
    };

    // ── Date range → git --since/--until ──────────────────────────────────────────

    [Fact]
    public async Task Date_filter_passes_day_start_and_whole_day_end_to_git()
    {
        var git = new FakeGitService();
        var vm = new WorkspaceViewModel(git, Item());
        await vm.ShowHistoryCommand.ExecuteAsync(null);

        var day = new DateTime(2026, 7, 17);
        vm.History.SinceDate = day;
        vm.History.UntilDate = day;
        await vm.History.HistoryLoad;

        var offset = DateTimeOffset.Now.Offset;
        Assert.Equal(new DateTimeOffset(2026, 7, 17, 0, 0, 0, offset), git.LastSince);
        Assert.Equal(new DateTimeOffset(2026, 7, 17, 23, 59, 59, offset), git.LastUntil);
    }

    [Fact]
    public async Task Date_filter_hides_the_lane_graph_and_clearing_restores_it()
    {
        var git = new FakeGitService();
        var vm = new WorkspaceViewModel(git, Item());
        await vm.ShowHistoryCommand.ExecuteAsync(null);
        Assert.True(vm.History.ShowGraph);

        vm.History.SinceDate = new DateTime(2026, 7, 1);
        await vm.History.HistoryLoad;
        Assert.True(vm.History.HasDateFilter);
        Assert.False(vm.History.ShowGraph);   // --since drops middle commits → not parent-closed

        vm.History.ClearDateFilterCommand.Execute(null);
        await vm.History.HistoryLoad;
        Assert.False(vm.History.HasDateFilter);
        Assert.True(vm.History.ShowGraph);
        Assert.Null(git.LastSince);
        Assert.Null(git.LastUntil);
    }

    [Fact]
    public async Task Last7Days_preset_sets_a_week_ending_today_and_reloads()
    {
        var git = new FakeGitService();
        var vm = new WorkspaceViewModel(git, Item());
        await vm.ShowHistoryCommand.ExecuteAsync(null);

        vm.History.Last7DaysCommand.Execute(null);
        await vm.History.HistoryLoad;

        Assert.Equal(DateTime.Today.AddDays(-6), vm.History.SinceDate);
        Assert.Equal(DateTime.Today, vm.History.UntilDate);
        Assert.True(vm.History.HasDateFilter);
        Assert.NotNull(git.LastSince);
        Assert.NotNull(git.LastUntil);
    }

    // ── Load more keeps paging past a client-side filter ──────────────────────────

    [Fact]
    public async Task Load_more_keeps_paging_until_a_client_side_filter_yields_matches()
    {
        // 700 commits; only the deepest 80 ("special") match — beyond two default pages of 300.
        var git = new FakeGitService();
        const int total = 700;
        for (var i = 0; i < total; i++)
        {
            git.StubCommits.Add(Commit(i, total, subject: i >= 620 ? $"special {i}" : $"work {i}"));
        }

        var vm = new WorkspaceViewModel(git, Item());
        await vm.History.LoadHistoryAsync();          // first page: 300 commits, all "work"
        Assert.Equal(300, vm.History.Commits.Count);

        vm.History.MessageFilter = "special";         // client-side; nothing matches in the first page
        Assert.Empty(vm.History.Commits);
        Assert.True(vm.History.HasMoreCommits);

        await vm.History.LoadMoreCommitsCommand.ExecuteAsync(null);

        // One click paged 300→600→900(=all) until matches appeared, instead of stopping at 600 with
        // nothing (the old single-page behaviour).
        Assert.NotEmpty(vm.History.Commits);
        Assert.All(vm.History.Commits, c => Assert.Contains("special", c.Subject));
    }

    [Fact]
    public async Task Load_more_runs_once_when_no_client_side_filter_is_active()
    {
        var git = new FakeGitService();
        for (var i = 0; i < 700; i++)
        {
            git.StubCommits.Add(Commit(i, 700));
        }

        var vm = new WorkspaceViewModel(git, Item());
        await vm.History.LoadHistoryAsync();
        Assert.Equal(300, vm.History.Commits.Count);

        await vm.History.LoadMoreCommitsCommand.ExecuteAsync(null);

        Assert.Equal(600, vm.History.Commits.Count);   // exactly one more page — unchanged behaviour
    }

    // ── Configurable page size ────────────────────────────────────────────────────

    [Fact]
    public async Task History_uses_the_configured_page_size()
    {
        var git = new FakeGitService();
        for (var i = 0; i < 200; i++)
        {
            git.StubCommits.Add(Commit(i, 200));
        }

        var settings = new FakeSettingsService();
        settings.Current.HistoryPageSize = 75;
        var vm = new WorkspaceViewModel(git, Item(), settings);

        await vm.History.Load();

        Assert.Equal(75, git.LastMaxCount);
        Assert.Equal(75, vm.History.Commits.Count);
    }

    [Fact]
    public async Task Configured_page_size_is_clamped_to_a_sane_minimum()
    {
        var git = new FakeGitService();
        for (var i = 0; i < 200; i++)
        {
            git.StubCommits.Add(Commit(i, 200));
        }

        var settings = new FakeSettingsService();
        settings.Current.HistoryPageSize = 10;     // below the floor
        var vm = new WorkspaceViewModel(git, Item(), settings);

        await vm.History.Load();

        Assert.Equal(50, git.LastMaxCount);        // clamped up to 50
    }

    [Fact]
    public void Settings_view_model_clamps_the_page_size_both_ways()
    {
        var settings = new FakeSettingsService();
        var vm = new SettingsViewModel(settings, new UpdateService("0.1.0"));

        vm.HistoryPageSize = 5;
        Assert.Equal(50, vm.HistoryPageSize);
        Assert.Equal(50, settings.Current.HistoryPageSize);

        vm.HistoryPageSize = 100_000;
        Assert.Equal(2000, vm.HistoryPageSize);
    }

    [Fact]
    public void History_page_size_survives_the_settings_round_trip()
    {
        var settings = new AppSettings { HistoryPageSize = 750 };

        var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
        var restored = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);

        Assert.Equal(750, restored!.HistoryPageSize);
    }
}
