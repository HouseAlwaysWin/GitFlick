using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>The Tags menu: create at HEAD, push all, delete local, delete remote.</summary>
public class TagCommandTests
{
    private static WorkspaceViewModel ForFake(out FakeGitService git)
    {
        git = new FakeGitService();
        return new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));
    }

    [Fact]
    public async Task Create_tag_tags_HEAD_with_the_entered_name()
    {
        var vm = ForFake(out var git);
        vm.PromptTagName = () => Task.FromResult<string?>("v1.0");

        await vm.CreateTagCommand.ExecuteAsync(null);

        Assert.Contains("tag v1.0 HEAD", git.Operations);
    }

    [Fact]
    public async Task Per_tag_delete_targets_the_tag_name()
    {
        var vm = ForFake(out var git);

        await vm.DeleteTagCommand.ExecuteAsync(new GitTag("v1.0"));

        Assert.Contains("tag -d v1.0", git.Operations);
    }

    [Fact]
    public async Task Delete_remote_tag_uses_the_first_remote()
    {
        var vm = ForFake(out var git);
        git.StubRemotes.Add("origin");

        await vm.DeleteRemoteTagCommand.ExecuteAsync(new GitTag("v1.0"));

        Assert.Contains("push origin --delete v1.0", git.Operations);
    }

    [Fact]
    public async Task Delete_remote_tag_reports_when_there_is_no_remote()
    {
        var vm = ForFake(out var git);   // no remotes

        await vm.DeleteRemoteTagCommand.ExecuteAsync(new GitTag("v1.0"));

        Assert.DoesNotContain(git.Operations, o => o.StartsWith("push"));
    }

    [Fact]
    public async Task Push_tags_needs_tags_and_a_remote()
    {
        var vm = ForFake(out var git);

        // No tags -> guarded no-op.
        await vm.PushTagsCommand.ExecuteAsync(null);
        Assert.DoesNotContain("push origin --tags", git.Operations);

        git.StubTags.Add(new GitTag("v1.0"));
        git.StubRemotes.Add("origin");
        await vm.RefreshAsync();

        await vm.PushTagsCommand.ExecuteAsync(null);
        Assert.Contains("push origin --tags", git.Operations);
    }
}
