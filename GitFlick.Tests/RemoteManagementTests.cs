using System.Linq;
using System.Threading.Tasks;
using GitFlick.Services;

namespace GitFlick.Tests;

/// <summary>Adding, listing and removing remotes against real git.</summary>
public class RemoteManagementTests
{
    private readonly GitService _git = new();

    [Fact]
    public async Task Add_then_list_then_remove()
    {
        using var repo = new TestRepo();
        Assert.Empty(await _git.GetRemoteListAsync(repo.Path));

        Assert.True((await _git.AddRemoteAsync(repo.Path, "origin", "https://example.com/x.git")).Succeeded);

        var one = Assert.Single(await _git.GetRemoteListAsync(repo.Path));
        Assert.Equal("origin", one.Name);
        Assert.Equal("https://example.com/x.git", one.Url);

        Assert.True((await _git.RemoveRemoteAsync(repo.Path, "origin")).Succeeded);
        Assert.Empty(await _git.GetRemoteListAsync(repo.Path));
    }

    [Fact]
    public async Task Lists_several_remotes_with_their_urls()
    {
        using var repo = new TestRepo();
        await _git.AddRemoteAsync(repo.Path, "origin", "https://example.com/o.git");
        await _git.AddRemoteAsync(repo.Path, "upstream", "git@github.com:u/x.git");

        var list = await _git.GetRemoteListAsync(repo.Path);

        Assert.Equal(2, list.Count);
        // A scp-style URL (git@…:…) has no "(fetch)"-confusing spaces mid-URL — parse must keep it whole.
        Assert.Contains(list, r => r.Name == "upstream" && r.Url == "git@github.com:u/x.git");
        Assert.Contains(list, r => r.Name == "origin" && r.Url == "https://example.com/o.git");
    }

    [Fact]
    public async Task Adding_a_duplicate_name_fails_and_does_not_change_the_list()
    {
        using var repo = new TestRepo();
        await _git.AddRemoteAsync(repo.Path, "origin", "https://example.com/o.git");

        var dup = await _git.AddRemoteAsync(repo.Path, "origin", "https://example.com/other.git");

        Assert.False(dup.Succeeded);
        Assert.Equal("https://example.com/o.git", Assert.Single(await _git.GetRemoteListAsync(repo.Path)).Url);
    }
}
