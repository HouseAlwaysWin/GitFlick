using System;
using System.Threading;
using System.Threading.Tasks;
using GitFlick.Models;

namespace GitFlick.Services;

/// <summary>
/// Routes ✨ Generate to whichever engine settings select — the built-in in-process model by
/// default, or an external Ollama server. Both engines stay constructed (they're cheap until
/// used) so switching in ⚙ takes effect on the next generate.
/// </summary>
public sealed class RoutingCommitMessageGenerator : ICommitMessageGenerator, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly LlamaCommitMessageGenerator _builtin;
    private readonly OllamaCommitMessageGenerator _ollama;

    public RoutingCommitMessageGenerator(ISettingsService settings)
    {
        _settings = settings;
        _builtin = new LlamaCommitMessageGenerator(settings);
        _ollama = new OllamaCommitMessageGenerator(settings);
    }

    public Task<string> GenerateAsync(string diff, CancellationToken cancellationToken = default) =>
        _settings.Current.AiEngine == CommitAiEngine.Ollama
            ? _ollama.GenerateAsync(diff, cancellationToken)
            : _builtin.GenerateAsync(diff, cancellationToken);

    public void Dispose() => _builtin.Dispose();
}
