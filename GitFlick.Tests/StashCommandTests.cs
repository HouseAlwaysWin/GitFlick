using System.IO;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>The full stash command set: create variants, latest/all actions, and per-entry actions.</summary>
public class StashCommandTests
{
    private static WorkspaceViewModel ForFake(out FakeGitService git)
    {
        git = new FakeGitService();
        return new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));
    }

    [Fact]
    public async Task Stash_variants_pass_the_right_flags()
    {
        var vm = ForFake(out var git);

        await vm.StashPushCommand.ExecuteAsync(null);
        await vm.StashUntrackedCommand.ExecuteAsync(null);
        await vm.StashStagedCommand.ExecuteAsync(null);

        Assert.Contains("stash push", git.Operations);
        Assert.Contains("stash push -u", git.Operations);
        Assert.Contains("stash push --staged", git.Operations);
    }

    [Fact]
    public async Task Per_stash_actions_target_the_entrys_index()
    {
        var vm = ForFake(out var git);
        var entry = new StashEntry(2, "WIP on main");

        await vm.ApplyStashCommand.ExecuteAsync(entry);
        await vm.PopStashAtCommand.ExecuteAsync(entry);
        await vm.DropStashCommand.ExecuteAsync(entry);

        Assert.Contains("stash apply 2", git.Operations);
        Assert.Contains("stash pop 2", git.Operations);
        Assert.Contains("stash drop 2", git.Operations);
    }

    [Fact]
    public async Task Latest_and_all_actions_need_a_stash_to_exist()
    {
        var vm = ForFake(out var git);

        // No stashes yet — guarded no-ops.
        await vm.ApplyLatestStashCommand.ExecuteAsync(null);
        await vm.StashPopCommand.ExecuteAsync(null);
        await vm.DropAllStashesCommand.ExecuteAsync(null);
        Assert.DoesNotContain("stash apply 0", git.Operations);
        Assert.DoesNotContain("stash clear", git.Operations);

        // With a stash present, they fire.
        git.StubStashes.Add(new StashEntry(0, "WIP on main"));
        await vm.RefreshAsync();

        await vm.ApplyLatestStashCommand.ExecuteAsync(null);
        await vm.DropAllStashesCommand.ExecuteAsync(null);

        Assert.Contains("stash apply 0", git.Operations);
        Assert.Contains("stash clear", git.Operations);
    }

    [Fact]
    public async Task Viewing_a_stash_loads_its_patch_into_the_diff()
    {
        var vm = ForFake(out _);

        await vm.ViewStashCommand.ExecuteAsync(new StashEntry(1, "WIP on main"));

        Assert.Contains("stash 1", vm.DiffText);   // the fake returns "diff for stash 1"
    }
}
