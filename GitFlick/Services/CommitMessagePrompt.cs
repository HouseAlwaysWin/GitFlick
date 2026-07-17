using System;

namespace GitFlick.Services;

/// <summary>The shared prompt and output cleanup both engines (built-in and Ollama) use.</summary>
public static class CommitMessagePrompt
{
    public const int MaxDiffChars = 8000;

    public static string Build(string diff)
    {
        var trimmed = diff.Length > MaxDiffChars ? diff[..MaxDiffChars] + "\n… (diff truncated)" : diff;
        return
            "Write a git commit message for the staged changes below.\n" +
            "- One short imperative summary line (~50-72 chars). Use Conventional Commits " +
            "(feat:, fix:, refactor:, docs:, test:, chore:) when it fits.\n" +
            "- If useful, add a blank line then 1-3 short bullet points.\n" +
            "- Output ONLY the commit message. No code fences, no preamble.\n\n" +
            "Diff:\n" + trimmed;
    }

    /// <summary>
    /// Strips wrappers small local models add: a leading &lt;think&gt; block (Qwen3 emits an empty
    /// one even with /no_think) and code fences.
    /// </summary>
    public static string Clean(string message)
    {
        var text = message.Trim();

        var thinkEnd = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (thinkEnd >= 0)
        {
            text = text[(thinkEnd + "</think>".Length)..].Trim();
        }

        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBreak = text.IndexOf('\n');
            if (firstBreak >= 0)
            {
                text = text[(firstBreak + 1)..];
            }

            if (text.EndsWith("```", StringComparison.Ordinal))
            {
                text = text[..^3];
            }
        }

        return text.Trim();
    }
}
