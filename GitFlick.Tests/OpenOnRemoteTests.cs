using System.IO;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>The Open-on-remote commands wire the remote URL through RemoteUrlBuilder to open/copy.</summary>
public class OpenOnRemoteTests
{
    private static WorkspaceViewModel ForFake(out FakeGitService git)
    {
        git = new FakeGitService();
        return new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));
    }

    private static CommitInfo Commit(string sha) =>
        new() { Sha = sha, Parents = [], Author = "A", When = default, Subject = "s" };

    [Fact]
    public async Task Open_commit_builds_and_opens_the_web_url()
    {
        var vm = ForFake(out var git);
        git.StubRemotes.Add("origin");
        git.StubRemoteUrl = "git@github.com:owner/repo.git";
        string? opened = null;
        vm.OpenUrlInBrowser = u => opened = u;
        vm.SelectedCommit = Commit("abc123");

        await vm.OpenCommitOnRemoteCommand.ExecuteAsync(null);

        Assert.Equal("https://github.com/owner/repo/commit/abc123", opened);
    }

    [Fact]
    public async Task Copy_commit_link_uses_the_clipboard_delegate_and_gitlab_scheme()
    {
        var vm = ForFake(out var git);
        git.StubRemotes.Add("origin");
        git.StubRemoteUrl = "https://gitlab.com/g/repo.git";
        string? copied = null;
        vm.SetClipboardText = t => { copied = t; return Task.CompletedTask; };
        vm.SelectedCommit = Commit("def");

        await vm.CopyCommitLinkCommand.ExecuteAsync(null);

        Assert.Equal("https://gitlab.com/g/repo/-/commit/def", copied);
    }

    [Fact]
    public async Task No_remote_reports_and_does_not_open()
    {
        var vm = ForFake(out _);   // no remotes configured
        var opened = false;
        vm.OpenUrlInBrowser = _ => opened = true;
        vm.SelectedCommit = Commit("abc");

        await vm.OpenCommitOnRemoteCommand.ExecuteAsync(null);

        Assert.False(opened);
    }
}
