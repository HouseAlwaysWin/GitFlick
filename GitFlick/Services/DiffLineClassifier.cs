using System;

namespace GitFlick.Services;

public enum DiffLineKind
{
    /// <summary>"diff --git", "index", "+++", "---", "Binary files ..." — the file preamble.</summary>
    Header,

    /// <summary>"@@ -1,4 +1,6 @@"</summary>
    Hunk,

    Added,
    Removed,
    Context,
}

/// <summary>
/// Classifies one line of a unified diff. Kept separate from the renderer so the rules are
/// testable — the "+++"-before-"+" ordering is exactly the kind of thing that silently rots.
/// </summary>
public static class DiffLineClassifier
{
    public static DiffLineKind Classify(string line)
    {
        if (line.Length == 0)
        {
            return DiffLineKind.Context;
        }

        if (line.StartsWith("@@", StringComparison.Ordinal))
        {
            return DiffLineKind.Hunk;
        }

        // Must be tested before the bare '+'/'-' cases: these are file headers, not content.
        if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
        {
            return DiffLineKind.Header;
        }

        if (IsPreamble(line))
        {
            return DiffLineKind.Header;
        }

        return line[0] switch
        {
            '+' => DiffLineKind.Added,
            '-' => DiffLineKind.Removed,
            _ => DiffLineKind.Context,
        };
    }

    private static bool IsPreamble(string line) =>
        line.StartsWith("diff ", StringComparison.Ordinal)
        || line.StartsWith("index ", StringComparison.Ordinal)
        || line.StartsWith("new file mode", StringComparison.Ordinal)
        || line.StartsWith("deleted file mode", StringComparison.Ordinal)
        || line.StartsWith("old mode", StringComparison.Ordinal)
        || line.StartsWith("new mode", StringComparison.Ordinal)
        || line.StartsWith("similarity index", StringComparison.Ordinal)
        || line.StartsWith("rename from", StringComparison.Ordinal)
        || line.StartsWith("rename to", StringComparison.Ordinal)
        || line.StartsWith("Binary files", StringComparison.Ordinal)
        || line.StartsWith("\\ No newline", StringComparison.Ordinal);
}
