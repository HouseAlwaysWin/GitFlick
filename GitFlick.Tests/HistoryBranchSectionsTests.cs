using System;
using System.Linq;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>The History branch filter split into LOCAL / REMOTE sections, each independently collapsible.</summary>
public class HistoryBranchSectionsTests
{
    private static string Sha(int i) => i.ToString("x").PadLeft(40, '0');

    private static CommitInfo Commit(int i, params (string Name, bool Remote)[] refs) => new()
    {
        Sha = Sha(i),
        Parents = i == 0 ? Array.Empty<string>() : new[] { Sha(i - 1) },
        Author = "Dev",
        When = DateTimeOffset.FromUnixTimeSeconds(2_000_000 - i),
        Subject = "commit " + i,
        Refs = refs.Select(r => new GitRef(r.Name, r.Remote ? GitRefKind.RemoteBranch : GitRefKind.LocalBranch)).ToList(),
    };

    private static async Task<WorkspaceViewModel> Loaded()
    {
        var git = new FakeGitService();
        git.StubCommits.Add(Commit(2, ("origin/feature", true)));   // newest first
        git.StubCommits.Add(Commit(1, ("feature", false)));
        git.StubCommits.Add(Commit(0, ("main", false), ("origin/main", true)));

        var vm = new WorkspaceViewModel(git, new RepositoryItem("r", System.IO.Path.GetTempPath()));
        await vm.History.LoadHistoryAsync();
        return vm;
    }

    [Fact]
    public async Task Branches_split_into_local_and_remote_sections()
    {
        var vm = await Loaded();

        Assert.True(vm.History.HasLocalBranches);
        Assert.True(vm.History.HasRemoteBranches);
        Assert.True(vm.History.ShowLocalBranches);    // both sections shown by default
        Assert.True(vm.History.ShowRemoteBranches);

        Assert.All(vm.History.FilteredLocalBranchFilters, b => Assert.False(b.IsRemote));
        Assert.All(vm.History.FilteredRemoteBranchFilters, b => Assert.True(b.IsRemote));

        Assert.Contains(vm.History.FilteredLocalBranchFilters, b => b.Name == "main");
        Assert.Contains(vm.History.FilteredLocalBranchFilters, b => b.Name == "feature");
        Assert.Contains(vm.History.FilteredRemoteBranchFilters, b => b.Name == "origin/main");
        Assert.Contains(vm.History.FilteredRemoteBranchFilters, b => b.Name == "origin/feature");
    }

    [Fact]
    public async Task The_search_box_narrows_both_sections()
    {
        var vm = await Loaded();
        vm.History.BranchFilterSearch = "feature";

        Assert.Contains(vm.History.FilteredLocalBranchFilters, b => b.Name == "feature");
        Assert.DoesNotContain(vm.History.FilteredLocalBranchFilters, b => b.Name == "main");
        Assert.Contains(vm.History.FilteredRemoteBranchFilters, b => b.Name == "origin/feature");
        Assert.DoesNotContain(vm.History.FilteredRemoteBranchFilters, b => b.Name == "origin/main");
    }

    [Fact]
    public async Task A_collapsed_section_still_applies_its_selection()
    {
        var vm = await Loaded();
        vm.History.ShowRemoteBranches = false;        // fold the REMOTE section away

        vm.History.BranchFilters.Single(b => b.Name == "origin/main").IsSelected = true;

        Assert.True(vm.History.HasBranchFilter);       // the tick still filters despite the collapse
        Assert.Single(vm.History.Commits);             // only origin/main's commit is reachable
    }

    [Fact]
    public async Task A_repo_with_no_remotes_hides_the_remote_section()
    {
        var git = new FakeGitService();
        git.StubCommits.Add(Commit(0, ("main", false)));
        var vm = new WorkspaceViewModel(git, new RepositoryItem("r", System.IO.Path.GetTempPath()));
        await vm.History.LoadHistoryAsync();

        Assert.True(vm.History.HasLocalBranches);
        Assert.False(vm.History.HasRemoteBranches);
        Assert.Empty(vm.History.FilteredRemoteBranchFilters);
    }
}
