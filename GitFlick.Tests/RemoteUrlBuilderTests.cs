using GitFlick.Services;

namespace GitFlick.Tests;

/// <summary>Turning git remote URLs into GitHub/GitLab web links.</summary>
public class RemoteUrlBuilderTests
{
    [Theory]
    [InlineData("git@github.com:owner/repo.git")]
    [InlineData("git@github.com:owner/repo")]
    [InlineData("https://github.com/owner/repo.git")]
    [InlineData("https://user@github.com/owner/repo")]
    [InlineData("ssh://git@github.com/owner/repo.git")]
    [InlineData("ssh://git@github.com:22/owner/repo.git")]
    public void GitHub_forms_all_resolve_to_the_same_web_base(string remote)
    {
        var info = RemoteUrlBuilder.Parse(remote);
        Assert.NotNull(info);
        Assert.Equal("https://github.com/owner/repo", info!.WebBase);
        Assert.False(info.IsGitLab);
    }

    [Fact]
    public void GitHub_commit_file_branch_links()
    {
        const string r = "git@github.com:owner/repo.git";
        Assert.Equal("https://github.com/owner/repo/commit/abc123", RemoteUrlBuilder.Commit(r, "abc123"));
        Assert.Equal("https://github.com/owner/repo/tree/feature/x", RemoteUrlBuilder.Branch(r, "feature/x"));
        Assert.Equal("https://github.com/owner/repo/blob/main/src/App.cs", RemoteUrlBuilder.File(r, "main", "src/App.cs"));
        Assert.Equal("https://github.com/owner/repo/blob/main/src/App.cs#L42", RemoteUrlBuilder.File(r, "main", "src/App.cs", 42));
    }

    [Fact]
    public void GitLab_uses_the_dash_infix_and_supports_subgroups()
    {
        const string r = "git@gitlab.com:group/subgroup/repo.git";
        var info = RemoteUrlBuilder.Parse(r);
        Assert.True(info!.IsGitLab);
        Assert.Equal("https://gitlab.com/group/subgroup/repo", info.WebBase);
        Assert.Equal("https://gitlab.com/group/subgroup/repo/-/commit/def", RemoteUrlBuilder.Commit(r, "def"));
        Assert.Equal("https://gitlab.com/group/subgroup/repo/-/blob/main/a.txt", RemoteUrlBuilder.File(r, "main", "a.txt"));
        Assert.Equal("https://gitlab.com/group/subgroup/repo/-/tree/main", RemoteUrlBuilder.Branch(r, "main"));
    }

    [Fact]
    public void Path_segments_with_spaces_or_cjk_are_encoded()
    {
        var url = RemoteUrlBuilder.File("git@github.com:o/r.git", "main", "docs/報告 稿.md");
        Assert.NotNull(url);
        Assert.DoesNotContain(" ", url);
        Assert.Contains("/blob/main/docs/", url);   // path structure preserved
    }

    [Theory]
    [InlineData("/home/user/repo")]        // local path — no web home
    [InlineData("")]
    [InlineData("   ")]
    public void Unparseable_remotes_yield_null(string remote)
    {
        Assert.Null(RemoteUrlBuilder.Parse(remote));
        Assert.Null(RemoteUrlBuilder.Commit(remote, "sha"));
    }
}
