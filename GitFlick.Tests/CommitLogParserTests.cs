using System.Linq;
using GitFlick.Models;
using GitFlick.Services;

namespace GitFlick.Tests;

public class CommitLogParserTests
{
    // Fields are NUL-separated: %H %P %an %aI %D %s
    private static string Record(string sha, string parents, string author, string date, string decorations, string subject)
        => string.Join('\0', sha, parents, author, date, decorations, subject);

    [Fact]
    public void Parses_a_plain_commit()
    {
        var line = Record("abc123def456", "", "Martin", "2026-07-14T10:30:00+08:00", "", "Initial commit");

        var commit = Assert.Single(CommitLogParser.Parse(line));

        Assert.Equal("abc123def456", commit.Sha);
        Assert.Equal("abc123d", commit.ShortSha);
        Assert.Empty(commit.Parents);
        Assert.Equal("Martin", commit.Author);
        Assert.Equal("Initial commit", commit.Subject);
        Assert.False(commit.IsMerge);
        Assert.False(commit.IsHead);
    }

    [Fact]
    public void Parses_parents_and_flags_merges()
    {
        var single = Assert.Single(CommitLogParser.Parse(Record("c", "p1", "a", "2026-01-01T00:00:00Z", "", "s")));
        Assert.Equal(["p1"], single.Parents);
        Assert.False(single.IsMerge);

        var merge = Assert.Single(CommitLogParser.Parse(Record("m", "p1 p2", "a", "2026-01-01T00:00:00Z", "", "s")));
        Assert.Equal(["p1", "p2"], merge.Parents);
        Assert.True(merge.IsMerge);
    }

    [Fact]
    public void Parses_decorations_into_branches_and_tags()
    {
        var line = Record(
            "abc", "", "a", "2026-01-01T00:00:00Z",
            "HEAD -> refs/heads/main, refs/remotes/origin/main, tag: refs/tags/v1.0",
            "Release");

        var commit = Assert.Single(CommitLogParser.Parse(line));

        Assert.True(commit.IsHead);
        Assert.Contains(commit.Refs, r => r.Name == "main" && r.Kind == GitRefKind.LocalBranch);
        Assert.Contains(commit.Refs, r => r.Name == "origin/main" && r.Kind == GitRefKind.RemoteBranch);
        Assert.Contains(commit.Refs, r => r.Name == "v1.0" && r.Kind == GitRefKind.Tag);
    }

    [Fact]
    public void Detached_head_is_marked_but_is_not_a_branch()
    {
        var commit = Assert.Single(CommitLogParser.Parse(
            Record("abc", "", "a", "2026-01-01T00:00:00Z", "HEAD", "s")));

        Assert.True(commit.IsHead);
        Assert.Empty(commit.Refs);
    }

    [Fact]
    public void Skips_the_origin_HEAD_alias()
    {
        // refs/remotes/origin/HEAD is a symbolic alias, not a branch — showing it is just noise.
        var commit = Assert.Single(CommitLogParser.Parse(
            Record("abc", "", "a", "2026-01-01T00:00:00Z", "refs/remotes/origin/HEAD, refs/remotes/origin/main", "s")));

        Assert.Equal(["origin/main"], commit.Refs.Select(r => r.Name));
    }

    [Fact]
    public void Keeps_cjk_authors_and_subjects_intact()
    {
        var commit = Assert.Single(CommitLogParser.Parse(
            Record("abc", "", "王小明", "2026-01-01T00:00:00Z", "", "新增報告檔案")));

        Assert.Equal("王小明", commit.Author);
        Assert.Equal("新增報告檔案", commit.Subject);
    }

    [Fact]
    public void Parses_many_commits_and_ignores_blank_lines()
    {
        var output = string.Join('\n',
            Record("a", "b", "x", "2026-01-01T00:00:00Z", "", "one"),
            Record("b", "", "x", "2026-01-01T00:00:00Z", "", "two"),
            "");

        Assert.Equal(2, CommitLogParser.Parse(output).Count);
    }
}
