using System.Collections.Generic;

namespace GitFlick.Services;

/// <summary>Helpers for reading git's line-oriented stdout.</summary>
internal static class GitOutput
{
    /// <summary>
    /// Enumerates the non-empty lines of git output: splits on '\n', strips a trailing '\r' (so it's
    /// newline-agnostic), and skips blank lines — the shape almost every porcelain parser needs. Lazy,
    /// so it avoids the intermediate <c>string[]</c> that <see cref="string.Split(char[])"/> allocates.
    /// </summary>
    public static IEnumerable<string> NonEmptyLines(string text)
    {
        var start = 0;

        while (start <= text.Length)
        {
            var newline = text.IndexOf('\n', start);
            var end = newline < 0 ? text.Length : newline;

            var lineEnd = end;
            while (lineEnd > start && text[lineEnd - 1] == '\r')
            {
                lineEnd--;
            }

            if (lineEnd > start)
            {
                yield return text[start..lineEnd];
            }

            if (newline < 0)
            {
                yield break;
            }

            start = newline + 1;
        }
    }
}
