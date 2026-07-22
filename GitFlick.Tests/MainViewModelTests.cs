using System.IO;
using System.Linq;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

public class MainViewModelTests : IDisposable
{
    private readonly string _repoA;
    private readonly string _repoB;
    private readonly string _notRepo;
    private readonly string _root;

    public MainViewModelTests()
    {
        // A unique temp tree per test run; xUnit gives each test its own instance.
        _root = Path.Combine(Path.GetTempPath(), "GitFlickTests", Path.GetRandomFileName());
        _repoA = MakeRepo("repo-alpha");
        _repoB = MakeRepo("repo-beta");
        _notRepo = Path.Combine(_root, "plain-folder");
        Directory.CreateDirectory(_notRepo);
    }

    private string MakeRepo(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        return path;
    }

    private MainViewModel NewViewModel(out FakeSettingsService settings)
    {
        settings = new FakeSettingsService();
        // Fake git: these tests cover palette/navigation, and a real subprocess would race
        // the temp-folder cleanup in Dispose.
        return new MainViewModel(settings, new FakeGitService());
    }

    [Fact]
    public void AddRepository_pins_a_valid_repo_and_persists()
    {
        var vm = NewViewModel(out var settings);

        vm.AddRepository(_repoA);

        Assert.Contains(_repoA, settings.Current.PinnedRepos);
        Assert.True(settings.SaveCount > 0);
        Assert.Contains(vm.Repos, r => r.Name == "repo-alpha");
        Assert.Empty(vm.StatusMessage);
    }

    [Fact]
    public void AddRepository_rejects_a_non_git_folder()
    {
        var vm = NewViewModel(out var settings);

        vm.AddRepository(_notRepo);

        Assert.Empty(settings.Current.PinnedRepos);
        Assert.Empty(vm.Repos);
        Assert.Contains("not a Git repository", vm.StatusMessage);
    }

    [Fact]
    public void AddRepository_ignores_a_duplicate()
    {
        var vm = NewViewModel(out var settings);

        vm.AddRepository(_repoA);
        vm.AddRepository(_repoA);

        Assert.Single(settings.Current.PinnedRepos);
        Assert.Contains("already pinned", vm.StatusMessage);
    }

    [Fact]
    public void OpenRepository_pins_a_new_repo_and_opens_it()
    {
        var vm = NewViewModel(out var settings);

        vm.OpenRepository(_repoA);

        Assert.Contains(_repoA, settings.Current.PinnedRepos);
        Assert.True(vm.IsRepoOpen);
        Assert.Equal("repo-alpha", vm.OpenRepo!.Name);
    }

    [Fact]
    public void OpenRepository_opens_an_already_pinned_repo_without_duplicating()
    {
        var vm = NewViewModel(out var settings);
        vm.AddRepository(_repoA);

        vm.OpenRepository(_repoA);

        Assert.Single(settings.Current.PinnedRepos);
        Assert.True(vm.IsRepoOpen);
        Assert.Equal("repo-alpha", vm.OpenRepo!.Name);
        Assert.Empty(vm.StatusMessage);
    }

    [Fact]
    public void OpenRepository_rejects_a_non_git_folder()
    {
        var vm = NewViewModel(out var settings);

        vm.OpenRepository(_notRepo);

        Assert.Empty(settings.Current.PinnedRepos);
        Assert.False(vm.IsRepoOpen);
        Assert.Contains("not a Git repository", vm.StatusMessage);
    }

    [Fact]
    public void RemoveSelected_unpins_and_persists()
    {
        var vm = NewViewModel(out var settings);
        vm.AddRepository(_repoA);
        vm.AddRepository(_repoB);
        vm.SelectedRepo = vm.Repos.First(r => r.Name == "repo-alpha");

        vm.RemoveSelected();

        Assert.DoesNotContain(_repoA, settings.Current.PinnedRepos);
        Assert.Contains(_repoB, settings.Current.PinnedRepos);
        Assert.DoesNotContain(vm.Repos, r => r.Name == "repo-alpha");
    }

    [Fact]
    public void SearchText_filters_by_fuzzy_match()
    {
        var vm = NewViewModel(out _);
        vm.AddRepository(_repoA);
        vm.AddRepository(_repoB);

        vm.SearchText = "beta";

        Assert.Single(vm.Repos);
        Assert.Equal("repo-beta", vm.Repos[0].Name);
    }

    [Fact]
    public void Name_match_ranks_above_a_path_only_match()
    {
        // Both test repos live under a path that itself contains "alpha"; only repo-alpha
        // matches "alpha" by name. It must come first.
        var vm = NewViewModel(out _);
        vm.AddRepository(_repoA);   // repo-alpha
        vm.AddRepository(_repoB);   // repo-beta, but shares the "...\GitFlickTests\..." parent

        vm.SearchText = "alpha";

        Assert.Equal("repo-alpha", vm.Repos[0].Name);
    }

    [Fact]
    public void Empty_search_shows_every_pin()
    {
        var vm = NewViewModel(out _);
        vm.AddRepository(_repoA);
        vm.AddRepository(_repoB);

        vm.SearchText = "beta";
        vm.SearchText = "";

        Assert.Equal(2, vm.Repos.Count);
    }

    [Fact]
    public void OpenSelected_switches_to_the_workspace_view()
    {
        var vm = NewViewModel(out _);
        vm.AddRepository(_repoA);
        vm.SelectedRepo = vm.Repos[0];

        vm.OpenSelected();

        Assert.True(vm.IsRepoOpen);
        Assert.False(vm.IsPaletteVisible);
        Assert.Equal("repo-alpha", vm.OpenRepo!.Name);

        vm.CloseRepo();
        Assert.True(vm.IsPaletteVisible);
    }

    [Fact]
    public void MoveSelection_clamps_at_the_ends()
    {
        var vm = NewViewModel(out _);
        vm.AddRepository(_repoA);
        vm.AddRepository(_repoB);
        vm.SelectedRepo = vm.Repos[0];

        vm.MoveSelection(-1);
        Assert.Equal(vm.Repos[0], vm.SelectedRepo);   // can't go above the top

        vm.MoveSelection(1);
        vm.MoveSelection(1);
        Assert.Equal(vm.Repos[^1], vm.SelectedRepo);  // can't go past the bottom
    }

    [Fact]
    public void The_hotkey_warning_can_be_dismissed()
    {
        var vm = NewViewModel(out _);
        vm.ReportHotkeyFailure("Ctrl+Alt+G is already in use by another application.");

        Assert.True(vm.HasHotkeyStatus);

        vm.DismissHotkeyStatusCommand.Execute(null);

        // Gone for good this session — it must not come back when the window is re-summoned.
        Assert.False(vm.HasHotkeyStatus);
        Assert.Empty(vm.HotkeyStatus);

        vm.ResetForSummon();
        Assert.False(vm.HasHotkeyStatus);
    }

    [Fact]
    public void ResetForSummon_clears_the_search_but_keeps_you_in_the_open_repo()
    {
        var vm = NewViewModel(out _);
        vm.AddRepository(_repoA);
        vm.SelectedRepo = vm.Repos[0];
        vm.OpenSelected();
        vm.SearchText = "xyz";

        vm.ResetForSummon();

        // Summoning returns you to what you were looking at; Esc is the way back to the palette.
        Assert.True(vm.IsRepoOpen);
        Assert.NotNull(vm.Workspace);
        Assert.Empty(vm.SearchText);
    }

    [Fact]
    public void Opening_repos_tracks_frequency_and_fills_the_frequent_panel()
    {
        var vm = NewViewModel(out var settings);
        vm.AddRepository(_repoA);
        vm.AddRepository(_repoB);

        Open(vm, _repoA);
        Open(vm, _repoA);
        Open(vm, _repoB);

        Assert.Equal(2, settings.Current.RepoOpenCounts[_repoA]);
        Assert.Equal(1, settings.Current.RepoOpenCounts[_repoB]);

        Assert.True(vm.HasFrequentRepos);
        Assert.Equal(2, vm.FrequentRepos.Count);
        Assert.Equal(_repoA, vm.FrequentRepos[0].Path);   // most-opened first
        Assert.Equal(2, vm.FrequentRepos[0].OpenCount);
        Assert.Equal(_repoB, vm.FrequentRepos[1].Path);
    }

    [Fact]
    public void No_frequent_repos_until_something_is_opened()
    {
        var vm = NewViewModel(out _);
        vm.AddRepository(_repoA);

        Assert.False(vm.HasFrequentRepos);
        Assert.Empty(vm.FrequentRepos);
    }

    [Fact]
    public void Removing_a_repo_drops_its_open_count()
    {
        var vm = NewViewModel(out var settings);
        vm.AddRepository(_repoA);
        Open(vm, _repoA);
        Assert.True(settings.Current.RepoOpenCounts.ContainsKey(_repoA));

        vm.SelectedRepo = vm.Repos.First(r => r.Path == _repoA);
        vm.RemoveSelected();

        Assert.False(settings.Current.RepoOpenCounts.ContainsKey(_repoA));
        Assert.False(vm.HasFrequentRepos);
    }

    [Fact]
    public void RemoveRepo_command_unpins_the_given_repo()
    {
        var vm = NewViewModel(out var settings);
        vm.AddRepository(_repoA);
        vm.AddRepository(_repoB);

        vm.RemoveRepoCommand.Execute(vm.Repos.First(r => r.Path == _repoA));

        Assert.DoesNotContain(vm.Repos, r => r.Path == _repoA);
        Assert.Contains(vm.Repos, r => r.Path == _repoB);
        Assert.DoesNotContain(_repoA, settings.Current.PinnedRepos);
    }

    private static void Open(MainViewModel vm, string path)
    {
        vm.SelectedRepo = vm.Repos.First(r => r.Path == path);
        vm.OpenSelected();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
