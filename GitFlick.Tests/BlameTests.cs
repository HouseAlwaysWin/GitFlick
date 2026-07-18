using System.IO;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.Services;
using GitFlick.ViewModels;

namespace GitFlick.Tests;

/// <summary>Blame: the porcelain parser (load-bearing), the VM, and a real-repo end-to-end check.</summary>
public class BlameTests
{
    private const string Sha1 = "1111111111111111111111111111111111111111";
    private const string Sha2 = "2222222222222222222222222222222222222222";
    private const string Zero = "0000000000000000000000000000000000000000";

    [Fact]
    public void Parser_attributes_each_line_and_reuses_cached_meta_for_repeat_hunks()
    {
        // Two contiguous lines from Sha1 (the 2nd hunk carries NO meta block — porcelain prints it
        // only the first time a commit appears), then one line from Sha2.
        var raw =
            Sha1 + " 1 1 2\n" +
            "author Alice\n" +
            "author-mail <alice@example.com>\n" +
            "author-time 1700000000\n" +
            "author-tz +0000\n" +
            "committer Alice\n" +
            "committer-mail <alice@example.com>\n" +
            "committer-time 1700000000\n" +
            "committer-tz +0000\n" +
            "summary first commit\n" +
            "filename foo.txt\n" +
            "\tline one\n" +
            Sha1 + " 2 2\n" +
            "\t第二行 CJK\n" +
            Sha2 + " 3 3 1\n" +
            "author Bob\n" +
            "author-mail <bob@example.com>\n" +
            "author-time 1700100000\n" +
            "author-tz +0800\n" +
            "committer Bob\n" +
            "committer-mail <bob@example.com>\n" +
            "committer-time 1700100000\n" +
            "committer-tz +0800\n" +
            "summary second commit\n" +
            "filename foo.txt\n" +
            "\tline three\n";

        var lines = BlameParser.Parse(raw);

        Assert.Equal(3, lines.Count);

        Assert.Equal(Sha1, lines[0].Sha);
        Assert.Equal("Alice", lines[0].Author);
        Assert.Equal("first commit", lines[0].Summary);
        Assert.Equal("line one", lines[0].Content);
        Assert.Equal(1, lines[0].LineNumber);

        // Repeat hunk from the same commit: inherits Alice + summary from the cache.
        Assert.Equal(Sha1, lines[1].Sha);
        Assert.Equal("Alice", lines[1].Author);
        Assert.Equal("first commit", lines[1].Summary);
        Assert.Equal("第二行 CJK", lines[1].Content);
        Assert.Equal(2, lines[1].LineNumber);

        Assert.Equal(Sha2, lines[2].Sha);
        Assert.Equal("Bob", lines[2].Author);
        Assert.Equal("second commit", lines[2].Summary);
        Assert.Equal("line three", lines[2].Content);
    }

    [Fact]
    public void Parser_flags_zero_sha_as_uncommitted()
    {
        var raw =
            Zero + " 1 1 1\n" +
            "author Not Committed Yet\n" +
            "author-mail <not.committed.yet>\n" +
            "author-time 1700200000\n" +
            "author-tz +0000\n" +
            "summary Version of foo.txt\n" +
            "filename foo.txt\n" +
            "\twork in progress\n";

        var lines = BlameParser.Parse(raw);

        Assert.Single(lines);
        Assert.True(lines[0].IsUncommitted);
        Assert.Equal("•••••••", lines[0].ShortSha);
        Assert.Equal(string.Empty, lines[0].WhenDisplay);
        Assert.Equal("work in progress", lines[0].Content);
    }

    [Fact]
    public void Parser_returns_empty_for_empty_output()
    {
        Assert.Empty(BlameParser.Parse(string.Empty));
    }

    [Fact]
    public async Task Load_fills_lines_and_passes_path_and_rev()
    {
        var git = new FakeGitService();
        git.StubBlame.Add(new BlameLine(Sha1, "Alice", default, "s", 1, "x"));
        var vm = new BlameViewModel(git, Path.GetTempPath(), "foo.txt", "HEAD~1");

        await vm.LoadAsync();

        Assert.Equal("foo.txt", git.LastBlamePath);
        Assert.Equal("HEAD~1", git.LastBlameRev);
        Assert.Single(vm.Lines);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public void CreateBlame_wires_the_path_through()
    {
        var git = new FakeGitService();
        var ws = new WorkspaceViewModel(git, new RepositoryItem("r", Path.GetTempPath()));

        var blame = ws.CreateBlame("a/b.cs");

        Assert.Equal("a/b.cs", blame.Path);
    }

    [Fact]
    public async Task Blame_reports_author_and_summary_from_a_real_repo()
    {
        using var repo = new TestRepo();
        repo.WriteFile("a.txt", "hello\nworld\n");
        repo.Git("add", "-A");
        repo.Git("commit", "-m", "add greeting");

        var lines = await new GitService().GetBlameAsync(repo.Path, "a.txt");

        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.Equal("GitFlick Test", line.Author));
        Assert.All(lines, line => Assert.Equal("add greeting", line.Summary));
        Assert.Equal("hello", lines[0].Content);
        Assert.Equal("world", lines[1].Content);
        Assert.False(lines[0].IsUncommitted);
    }
}
