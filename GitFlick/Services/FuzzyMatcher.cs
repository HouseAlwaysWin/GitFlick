using System;

namespace GitFlick.Services;

/// <summary>
/// Subsequence matching in the style of a command palette: every character of the pattern
/// must appear in order, and matches score higher when they run together or land on a word
/// boundary. Deliberately simple -- a handful of pinned repos does not need a real ranker.
/// </summary>
public static class FuzzyMatcher
{
    private const int MatchScore = 10;
    private const int ConsecutiveBonus = 6;
    private const int BoundaryBonus = 14;
    private const int GapPenalty = 1;

    public static bool TryMatch(string text, string pattern, out int score)
    {
        score = 0;

        if (pattern.Length == 0)
        {
            return true;
        }

        if (text.Length < pattern.Length)
        {
            return false;
        }

        var patternIndex = 0;
        var consecutive = 0;

        for (var i = 0; i < text.Length && patternIndex < pattern.Length; i++)
        {
            if (char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(pattern[patternIndex]))
            {
                score += MatchScore + (consecutive * ConsecutiveBonus);

                if (i == 0 || IsBoundary(text[i - 1]))
                {
                    score += BoundaryBonus;
                }

                consecutive++;
                patternIndex++;
            }
            else
            {
                consecutive = 0;
                score -= GapPenalty;
            }
        }

        if (patternIndex < pattern.Length)
        {
            score = 0;
            return false;
        }

        // Among equally-matching candidates, prefer the shorter one.
        score -= text.Length - pattern.Length;
        return true;
    }

    private static bool IsBoundary(char c) => c is '-' or '_' or ' ' or '.' or '/' or '\\';
}
