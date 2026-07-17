using System;
using System.IO;
using System.Threading.Tasks;
using GitFlick.Services;

namespace GitFlick.Tests;

/// <summary>
/// True end-to-end run of the built-in engine: load the smallest catalog GGUF and generate a
/// message for a real diff. Runs only when that model is already on disk (download it once via
/// ⚙ → "Qwen2.5 0.5B" or by flipping DownloadIfMissing below), so CI never pulls 400 MB.
/// </summary>
public class LlamaEndToEndTests
{
    // readonly (not const) so the download branch doesn't trip CS0162 unreachable-code.
    private static readonly bool DownloadIfMissing;

    [Fact]
    public async Task Builtin_engine_generates_a_message_from_a_diff()
    {
        var preset = CommitModelCatalog.Resolve("qwen2.5-0.5b");

        if (!CommitModelCatalog.IsDownloaded(preset))
        {
            if (!DownloadIfMissing)
            {
                return;   // model not present — nothing to exercise locally
            }

            await new ModelDownloader().DownloadAsync(preset);
        }

        var settings = new FakeSettingsService();
        settings.Current.BuiltinModelId = preset.Id;
        using var generator = new LlamaCommitMessageGenerator(settings);

        const string diff =
            """
            diff --git a/src/Calculator.cs b/src/Calculator.cs
            index 1234567..89abcde 100644
            --- a/src/Calculator.cs
            +++ b/src/Calculator.cs
            @@ -10,6 +10,12 @@ public class Calculator
                 public int Add(int a, int b) => a + b;

            +    public int Subtract(int a, int b) => a - b;
            +
            +    public int Multiply(int a, int b) => a * b;
            +
                 public override string ToString() => "Calculator";
             }
            """;

        var message = await generator.GenerateAsync(diff);

        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.True(generator.IsModelLoaded);

        // Drop the output where a human can eyeball the quality after a local run.
        await File.WriteAllTextAsync(
            Path.Combine(Path.GetTempPath(), "gitflick-llama-e2e.txt"), message);
    }
}
