using GitFlick.Services;

namespace GitFlick.Tests;

public class FuzzyMatcherTests
{
    [Theory]
    [InlineData("GitFlick", "gfl")]     // subsequence, not substring
    [InlineData("GitFlick", "flick")]   // substring
    [InlineData("repo-beta", "beta")]
    [InlineData("repo-beta", "rb")]     // spans the word boundary
    [InlineData("anything", "")]        // empty pattern matches everything
    public void Matches_valid_subsequences(string text, string pattern)
    {
        Assert.True(FuzzyMatcher.TryMatch(text, pattern, out _));
    }

    [Theory]
    [InlineData("GitFlick", "xyz")]
    [InlineData("GitFlick", "kilf")]    // right letters, wrong order
    [InlineData("abc", "abcd")]         // pattern longer than text
    public void Rejects_non_subsequences(string text, string pattern)
    {
        Assert.False(FuzzyMatcher.TryMatch(text, pattern, out var score));
        Assert.Equal(0, score);
    }

    [Fact]
    public void Consecutive_run_outscores_scattered_match()
    {
        FuzzyMatcher.TryMatch("readme", "rme", out var scattered);
        FuzzyMatcher.TryMatch("rme.txt", "rme", out var consecutive);

        Assert.True(consecutive > scattered);
    }

    [Fact]
    public void Word_boundary_start_outscores_mid_word()
    {
        FuzzyMatcher.TryMatch("my-server", "server", out var boundary);
        FuzzyMatcher.TryMatch("observers", "server", out var midWord);

        Assert.True(boundary > midWord);
    }
}
