using GitFlick.Services;

namespace GitFlick.Tests;

/// <summary>
/// What the command log keeps of git's output: the useful summary, minus the progress redraws, capped.
/// </summary>
public class CommandLogOutputTests
{
    [Fact]
    public void Keeps_the_push_summary_that_lives_only_in_the_output()
    {
        // git push writes its progress and result to stderr.
        const string stderr =
            "Enumerating objects: 13, done.\n" +
            "Counting objects:  50% (7/13)\r" +
            "Counting objects: 100% (13/13), done.\n" +
            "Writing objects:  20% (2/7)\r" +
            "Writing objects: 100% (7/7), 1.20 KiB, done.\n" +
            "To https://github.com/HouseAlwaysWin/GitFlick.git\n" +
            "   034be7d..1596341  master -> master\n";

        var output = GitService.BuildLogOutput(stdout: "", stderr: stderr);

        // The line that exists in no other commit — the whole reason to look here.
        Assert.Contains("034be7d..1596341  master -> master", output);
        Assert.Contains("Enumerating objects: 13, done.", output);
    }

    [Fact]
    public void Drops_the_transient_progress_redraws_but_keeps_the_final_of_each_phase()
    {
        const string stderr =
            "Counting objects:  50% (7/13)\r" +
            "Counting objects: 100% (13/13), done.\n";

        var output = GitService.BuildLogOutput("", stderr);

        Assert.DoesNotContain("50% (7/13)", output);   // intermediate redraw
        Assert.Contains("100% (13/13), done.", output); // the phase's final line survives
    }

    [Fact]
    public void Combines_stdout_and_stderr()
    {
        var output = GitService.BuildLogOutput("on stdout", "on stderr");

        Assert.Contains("on stdout", output);
        Assert.Contains("on stderr", output);
    }

    [Fact]
    public void Nothing_printed_is_empty()
    {
        Assert.Equal(string.Empty, GitService.BuildLogOutput("", ""));
        Assert.Equal(string.Empty, GitService.BuildLogOutput("  \n  ", "\r\n"));
    }

    [Fact]
    public void A_huge_output_is_truncated_keeping_the_tail()
    {
        // A push summary sits at the end, so the tail is the part worth keeping.
        var big = new string('x', 20_000) + "\nTAIL-MARKER";

        var output = GitService.BuildLogOutput(big, "");

        Assert.Contains("TAIL-MARKER", output);
        Assert.Contains("truncated", output);
        Assert.True(output.Length < 9000, $"expected a capped length, got {output.Length}");
    }
}
