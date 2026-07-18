using System.IO;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.Services;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>The reflog view: parsing, loading, and "reset to here" recovery.</summary>
public class ReflogTests
{
    [Fact]
    public void Parser_reads_selector_sha_description_and_date()
    {
        var raw =
            "HEAD@{0}\0abc123def456\0commit: add feature\02026-01-02T10:00:00+00:00\n" +
            "HEAD@{1}\0def456abc789\0checkout: moving from main to feature\02026-01-01T09:00:00+00:00\n";

        var entries = ReflogParser.Parse(raw);

        Assert.Equal(2, entries.Count);
        Assert.Equal("HEAD@{0}", entries[0].Selector);
        Assert.Equal("abc123d", entries[0].ShortSha);
        Assert.Equal("commit: add feature", entries[0].Description);
        Assert.Contains("checkout", entries[1].Description);
    }

    [Fact]
    public async Task Load_reflog_fills_the_collection()
    {
        var git = new FakeGitService();
        git.StubReflog.Add(new ReflogEntry("HEAD@{0}", "abc1234", "commit: x", default));
        var vm = new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));

        await vm.LoadReflogAsync();

        Assert.True(vm.HasReflog);
        Assert.Single(vm.Reflog);
    }

    [Fact]
    public async Task Reset_to_reflog_entry_records_the_reset()
    {
        var git = new FakeGitService();
        var vm = new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));
        vm.PromptResetMode = _ => Task.FromResult<GitResetMode?>(GitResetMode.Mixed);

        await vm.ResetToReflogCommand.ExecuteAsync(new ReflogEntry("HEAD@{1}", "deadbeef1234", "reset", default));

        Assert.Contains("reset Mixed deadbeef1234", git.Operations);
    }

    [Fact]
    public async Task Reflog_is_non_empty_after_activity()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a", "1"); repo.Git("add", "-A"); repo.Git("commit", "-m", "c1");
        repo.WriteFile("a", "2"); repo.Git("add", "-A"); repo.Git("commit", "-m", "c2");
        repo.Git("reset", "--hard", "HEAD~1");

        var log = await new GitService().GetReflogAsync(repo.Path);

        Assert.NotEmpty(log);
    }
}
