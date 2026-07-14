using GitFlick.Services;

namespace GitFlick.Tests;

public class DiffLineClassifierTests
{
    [Theory]
    [InlineData("+added line", DiffLineKind.Added)]
    [InlineData("+", DiffLineKind.Added)]
    [InlineData("-removed line", DiffLineKind.Removed)]
    [InlineData("-", DiffLineKind.Removed)]
    [InlineData(" context", DiffLineKind.Context)]
    [InlineData("", DiffLineKind.Context)]
    [InlineData("@@ -1,4 +1,6 @@", DiffLineKind.Hunk)]
    public void Classifies_content_lines(string line, DiffLineKind expected)
    {
        Assert.Equal(expected, DiffLineClassifier.Classify(line));
    }

    [Theory]
    // The ordering trap: these start with '+' / '-' but are headers, not content.
    [InlineData("+++ b/file.txt")]
    [InlineData("--- a/file.txt")]
    [InlineData("diff --git a/file.txt b/file.txt")]
    [InlineData("index 83db48f..bf269f4 100644")]
    [InlineData("new file mode 100644")]
    [InlineData("deleted file mode 100644")]
    [InlineData("rename from old.txt")]
    [InlineData("rename to new.txt")]
    [InlineData("Binary files a/x.png and b/x.png differ")]
    [InlineData("\\ No newline at end of file")]
    public void Classifies_headers(string line)
    {
        Assert.Equal(DiffLineKind.Header, DiffLineClassifier.Classify(line));
    }
}
