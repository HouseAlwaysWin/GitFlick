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
        return new MainViewModel(settings);
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
    public void ResetForSummon_clears_search_and_leaves_the_workspace()
    {
        var vm = NewViewModel(out _);
        vm.AddRepository(_repoA);
        vm.SelectedRepo = vm.Repos[0];
        vm.OpenSelected();
        vm.SearchText = "xyz";

        vm.ResetForSummon();

        Assert.True(vm.IsPaletteVisible);
        Assert.Empty(vm.SearchText);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
