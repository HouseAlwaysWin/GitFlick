using System.Threading;
using System.Threading.Tasks;
using GitFlick.Services;

namespace GitFlick.Tests;

/// <summary>A stand-in AI generator: returns a fixed message and records the diff it was given.</summary>
internal sealed class FakeCommitMessageGenerator : ICommitMessageGenerator
{
    public string Result { get; set; } = "feat: generated message";

    public string? LastDiff { get; private set; }

    public Task<string> GenerateAsync(string diff, CancellationToken cancellationToken = default)
    {
        LastDiff = diff;
        return Task.FromResult(Result);
    }
}
