using GitFlick.Services;

namespace GitFlick.Tests;

public class SideBySideDiffTests
{
    private const string ModifiedDiff =
        "diff --git a/f.txt b/f.txt\n" +
        "index abc..def 100644\n" +
        "--- a/f.txt\n" +
        "+++ b/f.txt\n" +
        "@@ -1,4 +1,4 @@\n" +
        " line1\n" +
        "-old2\n" +
        "+new2\n" +
        " line3\n" +
        " line4\n";

    [Fact]
    public void Pairs_a_removed_line_with_the_added_line_that_replaced_it()
    {
        var rows = SideBySideDiff.Build(ModifiedDiff);

        Assert.Equal(4, rows.Count);

        // context
        Assert.Equal("line1", rows[0].LeftText);
        Assert.Equal(DiffCellKind.Context, rows[0].LeftKind);
        Assert.Equal(1, rows[0].LeftLine);
        Assert.Equal(1, rows[0].RightLine);

        // the change, paired on one row
        Assert.Equal("old2", rows[1].LeftText);
        Assert.Equal(DiffCellKind.Removed, rows[1].LeftKind);
        Assert.Equal(2, rows[1].LeftLine);
        Assert.Equal("new2", rows[1].RightText);
        Assert.Equal(DiffCellKind.Added, rows[1].RightKind);
        Assert.Equal(2, rows[1].RightLine);

        Assert.Equal("line3", rows[2].LeftText);
        Assert.Equal(4, rows[3].LeftLine);
    }

    [Fact]
    public void A_pure_insertion_gets_an_empty_cell_on_the_old_side()
    {
        var diff =
            "@@ -1,2 +1,3 @@\n" +
            " a\n" +
            "+inserted\n" +
            " b\n";

        var rows = SideBySideDiff.Build(diff);

        Assert.Equal(3, rows.Count);
        Assert.Equal(DiffCellKind.Empty, rows[1].LeftKind);
        Assert.Null(rows[1].LeftLine);
        Assert.Equal("inserted", rows[1].RightText);
        Assert.Equal(DiffCellKind.Added, rows[1].RightKind);
        Assert.Equal(2, rows[1].RightLine);

        Assert.Equal("b", rows[2].LeftText);
        Assert.Equal(2, rows[2].LeftLine);
        Assert.Equal(3, rows[2].RightLine);
    }

    [Fact]
    public void Skips_headers_and_the_no_newline_marker()
    {
        var diff =
            "diff --git a/f b/f\n" +
            "--- a/f\n" +
            "+++ b/f\n" +
            "@@ -1 +1 @@\n" +
            "-only\n" +
            "\\ No newline at end of file\n" +
            "+only!\n";

        var rows = SideBySideDiff.Build(diff);

        Assert.Single(rows);
        Assert.Equal("only", rows[0].LeftText);
        Assert.Equal("only!", rows[0].RightText);
    }
}
