using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>The Tags menu: create at HEAD, push all, multi-select delete, and the on-remote marker.</summary>
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
    public async Task Delete_selected_removes_every_ticked_tag_in_one_call()
    {
        var vm = ForFake(out var git);
        git.StubTags.Add(new GitTag("v1.0"));
        git.StubTags.Add(new GitTag("v1.1"));
        git.StubTags.Add(new GitTag("v2.0"));
        await vm.RefreshAsync();

        vm.Tags[0].IsSelected = true;
        vm.Tags[2].IsSelected = true;
        Assert.True(vm.HasSelectedTags);
        Assert.Equal(2, vm.SelectedTagCount);

        await vm.DeleteSelectedTagsCommand.ExecuteAsync(null);

        Assert.Contains(git.Operations, o => o == "tag -d v1.0 v2.0");
    }

    [Fact]
    public async Task Delete_selected_on_remote_uses_the_first_remote()
    {
        var vm = ForFake(out var git);
        git.StubRemotes.Add("origin");
        git.StubTags.Add(new GitTag("v1.0"));
        git.StubTags.Add(new GitTag("v2.0"));
        await vm.RefreshAsync();

        vm.Tags[0].IsSelected = true;

        await vm.DeleteSelectedRemoteTagsCommand.ExecuteAsync(null);

        Assert.Contains("push origin --delete v1.0", git.Operations);
    }

    [Fact]
    public async Task Bulk_delete_is_a_no_op_when_nothing_is_ticked()
    {
        var vm = ForFake(out var git);
        git.StubTags.Add(new GitTag("v1.0"));
        await vm.RefreshAsync();

        await vm.DeleteSelectedTagsCommand.ExecuteAsync(null);

        Assert.DoesNotContain(git.Operations, o => o.StartsWith("tag -d"));
    }

    [Fact]
    public async Task Push_tags_needs_tags_and_a_remote()
    {
        var vm = ForFake(out var git);

        await vm.PushTagsCommand.ExecuteAsync(null);   // no tags -> no-op
        Assert.DoesNotContain("push origin --tags", git.Operations);

        git.StubTags.Add(new GitTag("v1.0"));
        git.StubRemotes.Add("origin");
        await vm.RefreshAsync();

        await vm.PushTagsCommand.ExecuteAsync(null);
        Assert.Contains("push origin --tags", git.Operations);
    }

    [Fact]
    public async Task Select_all_toggles_every_tag()
    {
        var vm = ForFake(out var git);
        git.StubTags.Add(new GitTag("v1.0"));
        git.StubTags.Add(new GitTag("v1.1"));
        git.StubTags.Add(new GitTag("v2.0"));
        await vm.RefreshAsync();

        Assert.False(vm.AllTagsSelected);

        vm.AllTagsSelected = true;
        Assert.True(vm.AllTagsSelected);
        Assert.Equal(3, vm.SelectedTagCount);
        Assert.All(vm.Tags, t => Assert.True(t.IsSelected));

        vm.AllTagsSelected = false;
        Assert.Equal(0, vm.SelectedTagCount);
        Assert.All(vm.Tags, t => Assert.False(t.IsSelected));
    }

    [Fact]
    public async Task Select_all_is_checked_only_when_every_tag_is_ticked()
    {
        var vm = ForFake(out var git);
        git.StubTags.Add(new GitTag("v1.0"));
        git.StubTags.Add(new GitTag("v2.0"));
        await vm.RefreshAsync();

        vm.Tags[0].IsSelected = true;       // partial
        Assert.False(vm.AllTagsSelected);

        vm.Tags[1].IsSelected = true;       // all
        Assert.True(vm.AllTagsSelected);
    }

    [Fact]
    public async Task Remote_check_marks_which_tags_are_on_the_remote()
    {
        var vm = ForFake(out var git);
        git.StubRemotes.Add("origin");
        git.StubTags.Add(new GitTag("v1.0"));
        git.StubTags.Add(new GitTag("v2.0"));
        git.StubRemoteTagNames.Add("v1.0");   // only v1.0 is on the remote
        await vm.RefreshAsync();

        await vm.CheckRemoteAsync();           // learns the remote tag set and marks the list

        var v10 = vm.Tags.First(t => t.Name == "v1.0");
        var v20 = vm.Tags.First(t => t.Name == "v2.0");
        Assert.True(v10.IsOnRemote);
        Assert.True(v20.IsLocalOnly);
    }
}
