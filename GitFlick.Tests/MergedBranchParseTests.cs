using GitFlick.Services;

namespace GitFlick.Tests;

public class MergedBranchParseTests
{
    [Theory]
    // GitHub PR merges: the "owner/" segment is dropped, branch slashes are kept.
    [InlineData("Merge pull request #32 from HouseAlwaysWin/claude/pin-opacity", "claude/pin-opacity")]
    [InlineData("Merge pull request #7 from acme/feature", "feature")]
    // git's own merge subjects: the quoted branch.
    [InlineData("Merge branch 'develop'", "develop")]
    [InlineData("Merge branch 'feature/x' into main", "feature/x")]
    [InlineData("Merge remote-tracking branch 'origin/hotfix'", "origin/hotfix")]
    public void Extracts_the_branch_from_a_merge_subject(string subject, string expected) =>
        Assert.Equal(expected, GitService.ParseMergedBranch(subject));

    [Theory]
    // Not a recognised merge subject → nothing to show.
    [InlineData("fix(build): repair the thing")]
    [InlineData("Merge pull request #9 from noslashowner")]   // no "owner/branch" split
    [InlineData("Merge branch without quotes")]
    [InlineData("")]
    public void Returns_empty_when_no_branch_can_be_read(string subject) =>
        Assert.Equal(string.Empty, GitService.ParseMergedBranch(subject));
}
