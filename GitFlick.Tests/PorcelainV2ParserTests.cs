using System.Linq;
using GitFlick.Models;
using GitFlick.Services;

namespace GitFlick.Tests;

public class PorcelainV2ParserTests
{
    [Fact]
    public void Parses_branch_header_with_ahead_behind()
    {
        var output = string.Join('\n',
            "# branch.oid 1111111111111111111111111111111111111111",
            "# branch.head main",
            "# branch.upstream origin/main",
            "# branch.ab +2 -1");

        var status = PorcelainV2Parser.Parse(output);

        Assert.Equal("main", status.BranchName);
        Assert.Equal("origin/main", status.Upstream);
        Assert.Equal(2, status.Ahead);
        Assert.Equal(1, status.Behind);
        Assert.False(status.IsDetached);
    }

    [Fact]
    public void Reports_detached_head_and_unborn_branch_as_null()
    {
        var detached = PorcelainV2Parser.Parse("# branch.oid abcd\n# branch.head (detached)");
        Assert.True(detached.IsDetached);
        Assert.Null(detached.BranchName);

        var unborn = PorcelainV2Parser.Parse("# branch.oid (initial)\n# branch.head main");
        Assert.Null(unborn.Oid);
        Assert.Equal("main", unborn.BranchName);
    }

    [Fact]
    public void Parses_a_cjk_path_literally_without_octal_escapes()
    {
        // With core.quotepath=false git emits the raw UTF-8 bytes; decoded, they are the string.
        var output = "1 A. N... 000000 100644 100644 0000000000000000000000000000000000000000 abc 測試檔案.txt";

        var status = PorcelainV2Parser.Parse(output);

        var entry = Assert.Single(status.Entries);
        Assert.Equal("測試檔案.txt", entry.Path);
        Assert.DoesNotContain("\\", entry.Path);   // no octal escaping leaked through
    }

    [Fact]
    public void Distinguishes_staged_from_unstaged()
    {
        var output = string.Join('\n',
            "1 A. N... 000000 100644 100644 000 aaa staged-new.txt",     // added, staged only
            "1 .M N... 100644 100644 100644 bbb bbb worktree-edit.txt",  // modified, unstaged only
            "1 MM N... 100644 100644 100644 ccc ddd both.txt");          // staged and further edited

        var status = PorcelainV2Parser.Parse(output);

        var staged = status.Staged.Select(e => e.Path).ToList();
        var unstaged = status.Unstaged.Select(e => e.Path).ToList();

        Assert.Contains("staged-new.txt", staged);
        Assert.DoesNotContain("staged-new.txt", unstaged);

        Assert.Contains("worktree-edit.txt", unstaged);
        Assert.DoesNotContain("worktree-edit.txt", staged);

        Assert.Contains("both.txt", staged);
        Assert.Contains("both.txt", unstaged);
    }

    [Fact]
    public void Parses_a_rename_with_its_original_path()
    {
        var output = "2 R. N... 100644 100644 100644 aaa aaa R100 new-name.txt\told-name.txt";

        var entry = Assert.Single(PorcelainV2Parser.Parse(output).Entries);

        Assert.Equal(GitChangeKind.Renamed, entry.Kind);
        Assert.Equal("new-name.txt", entry.Path);
        Assert.Equal("old-name.txt", entry.OriginalPath);
        Assert.True(entry.IsStaged);
    }

    [Fact]
    public void Classifies_untracked_and_ignored()
    {
        var output = "? untracked.txt\n! ignored.log";

        var status = PorcelainV2Parser.Parse(output);
        var untracked = status.Entries.Single(e => e.Kind == GitChangeKind.Untracked);
        var ignored = status.Entries.Single(e => e.Kind == GitChangeKind.Ignored);

        Assert.Equal("untracked.txt", untracked.Path);
        Assert.True(untracked.IsUnstaged);
        Assert.False(untracked.IsStaged);

        Assert.Equal("ignored.log", ignored.Path);
        Assert.False(ignored.IsStaged);
        Assert.False(ignored.IsUnstaged);
    }

    [Fact]
    public void Treats_unmerged_entries_as_conflicts()
    {
        var output = "u UU N... 100644 100644 100644 100644 aaa bbb ccc conflict.txt";

        var entry = Assert.Single(PorcelainV2Parser.Parse(output).Entries);

        Assert.Equal(GitChangeKind.Unmerged, entry.Kind);
        Assert.Equal("conflict.txt", entry.Path);
        Assert.True(entry.IsUnstaged);
    }

    [Fact]
    public void Handles_crlf_line_endings()
    {
        var status = PorcelainV2Parser.Parse("# branch.head main\r\n? a.txt\r\n");

        Assert.Equal("main", status.BranchName);
        Assert.Equal("a.txt", Assert.Single(status.Entries).Path);
    }

    [Fact]
    public void Empty_output_is_a_clean_repo()
    {
        var status = PorcelainV2Parser.Parse("# branch.head main");
        Assert.True(status.IsClean);
    }
}
