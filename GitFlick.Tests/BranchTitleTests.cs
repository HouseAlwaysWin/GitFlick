using System.IO;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>
/// What the title bar says about the checked-out branch. HEAD is always on a LOCAL branch — checking
/// out "origin/main" detaches — so the distinction worth showing is tracked vs never-pushed.
/// </summary>
public class BranchTitleTests
{
    private static WorkspaceViewModel ForFake(out FakeGitService git)
    {
        git = new FakeGitService();
        return new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));
    }

    [Fact]
    public void A_tracked_branch_names_its_upstream()
    {
        var vm = ForFake(out _);
        vm.BranchName = "main";
        vm.Upstream = "origin/main";

        Assert.Equal("main → origin/main", vm.BranchTitle);
    }

    [Fact]
    public void A_branch_that_was_never_pushed_says_so()
    {
        var vm = ForFake(out _);
        vm.BranchName = "feature";
        vm.Upstream = null;

        Assert.Equal("feature (local only)", vm.BranchTitle);
    }

    [Fact]
    public void A_detached_head_is_left_alone()
    {
        var vm = ForFake(out _);
        vm.BranchName = "(detached)";
        vm.IsDetachedHead = true;

        // No upstream to name and nothing "local only" about it — the state is the whole story.
        Assert.Equal("(detached)", vm.BranchTitle);
    }

    [Fact]
    public void No_branch_yields_nothing_to_show()
    {
        var vm = ForFake(out _);
        Assert.Equal(string.Empty, vm.BranchTitle);
    }

    [Fact]
    public void Setting_or_dropping_the_upstream_re_raises_the_title()
    {
        var vm = ForFake(out _);
        vm.BranchName = "main";

        var raised = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkspaceViewModel.BranchTitle))
            {
                raised++;
            }
        };

        // The title has to follow the upstream, not just the branch name — publishing a branch
        // changes only the upstream, and the bar would otherwise keep saying "local only".
        vm.Upstream = "origin/main";
        Assert.Equal(1, raised);
        Assert.Equal("main → origin/main", vm.BranchTitle);

        vm.Upstream = null;
        Assert.Equal(2, raised);
        Assert.Equal("main (local only)", vm.BranchTitle);
    }

    [Fact]
    public async Task It_follows_a_real_refresh()
    {
        var git = new FakeGitService();
        git.StubStatus = new GitStatus { BranchName = "main", Upstream = "origin/main" };
        var vm = new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));

        await vm.RefreshAsync();

        Assert.Equal("main → origin/main", vm.BranchTitle);
    }
}
