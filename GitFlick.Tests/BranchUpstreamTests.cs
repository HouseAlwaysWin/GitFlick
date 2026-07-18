using System.IO;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>Branch rename and upstream commands (act on the selected branch).</summary>
public class BranchUpstreamTests
{
    private static WorkspaceViewModel ForFake(out FakeGitService git)
    {
        git = new FakeGitService();
        return new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));
    }

    [Fact]
    public async Task Rename_records_branch_move_with_the_new_name()
    {
        var vm = ForFake(out var git);
        vm.SelectedBranch = new GitBranch { Name = "feature", IsCurrent = false };
        vm.PromptBranchName = () => Task.FromResult<string?>("feature-2");

        await vm.RenameBranchCommand.ExecuteAsync(null);

        Assert.Contains("branch -m feature feature-2", git.Operations);
    }

    [Fact]
    public async Task Set_upstream_records_the_picked_remote_branch()
    {
        var vm = ForFake(out var git);
        git.StubRemoteBranches.Add("origin/main");
        git.StubRemoteBranches.Add("origin/dev");
        vm.SelectedBranch = new GitBranch { Name = "feature", IsCurrent = true };
        vm.PromptPickRef = (items, _) => Task.FromResult<string?>(items[1]);   // origin/dev

        await vm.SetUpstreamCommand.ExecuteAsync(null);

        Assert.Contains("branch --set-upstream-to=origin/dev feature", git.Operations);
    }

    [Fact]
    public async Task Unset_upstream_records_the_clear()
    {
        var vm = ForFake(out var git);
        vm.SelectedBranch = new GitBranch { Name = "feature" };

        await vm.UnsetUpstreamCommand.ExecuteAsync(null);

        Assert.Contains("branch --unset-upstream feature", git.Operations);
    }

    [Fact]
    public async Task Set_upstream_reports_when_there_are_no_remote_branches()
    {
        var vm = ForFake(out var git);   // StubRemoteBranches empty
        vm.SelectedBranch = new GitBranch { Name = "feature" };
        vm.PromptPickRef = (_, _) => Task.FromResult<string?>("should-not-be-called");

        await vm.SetUpstreamCommand.ExecuteAsync(null);

        Assert.DoesNotContain(git.Operations, o => o.StartsWith("branch --set-upstream"));
    }
}
