using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Exceptions;
using LLama.Sampling;

namespace GitFlick.Services;

/// <summary>
/// The built-in engine: runs a downloaded GGUF model in-process via LLamaSharp (llama.cpp),
/// so generating commit messages needs no Ollama install and nothing leaves the machine.
/// Ported from GimmeCapture's LlamaSharpTranslationEngine: lazy load under a lock, serialized
/// inference, and an idle timer that frees the multi-GB weights after 5 minutes of no use.
/// </summary>
public sealed class LlamaCommitMessageGenerator : ICommitMessageGenerator, IDisposable
{
    private static readonly TimeSpan IdleUnloadAfter = TimeSpan.FromMinutes(5);

    private readonly ISettingsService _settings;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly SemaphoreSlim _inferLock = new(1, 1);
    private readonly object _timerGate = new();

    private LLamaWeights? _weights;
    private StatelessExecutor? _executor;
    private string? _loadedModelPath;
    private Timer? _idleTimer;

    public LlamaCommitMessageGenerator(ISettingsService settings)
    {
        _settings = settings;
    }

    internal bool IsModelLoaded => _executor is not null;

    public async Task<string> GenerateAsync(string diff, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(diff))
        {
            throw new InvalidOperationException("Nothing staged to describe.");
        }

        var preset = CommitModelCatalog.Resolve(_settings.Current.BuiltinModelId);
        if (!CommitModelCatalog.IsDownloaded(preset))
        {
            throw new InvalidOperationException(
                $"Model not downloaded yet — open ⚙ and download “{preset.DisplayName}” first.");
        }

        NotifyUse();
        await EnsureLoadedAsync(CommitModelCatalog.GetModelPath(preset), cancellationToken).ConfigureAwait(false);

        var prompt = BuildPrompt(preset, diff);

        var inference = new InferenceParams
        {
            MaxTokens = 220,
            // Stop at the turn boundary of whichever template the model uses.
            AntiPrompts = preset.Style == PromptStyle.Gemma
                ? ["<end_of_turn>", "<start_of_turn>"]
                : ["<|im_end|>", "<|im_start|>"],
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = 0.20f,
                TopK = 20,
                TopP = 0.60f,
                RepeatPenalty = 1.08f,
                Seed = 1337,
            },
        };

        var builder = new StringBuilder();
        await _inferLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var executor = _executor ?? throw new InvalidOperationException("Model failed to load.");
            await foreach (var token in executor.InferAsync(prompt, inference, cancellationToken).ConfigureAwait(false))
            {
                builder.Append(token);
            }
        }
        finally
        {
            _inferLock.Release();
            NotifyUse();   // restart the idle countdown after the run completes
        }

        var text = builder.ToString()
            .Replace("<|im_end|>", string.Empty, StringComparison.Ordinal)
            .Replace("<|im_start|>", string.Empty, StringComparison.Ordinal)
            .Replace("<end_of_turn>", string.Empty, StringComparison.Ordinal)
            .Replace("<start_of_turn>", string.Empty, StringComparison.Ordinal);
        return CommitMessagePrompt.Clean(text);
    }

    /// <summary>The instruction wrapped in the model's own chat template.</summary>
    private static string BuildPrompt(CommitModelPreset preset, string diff)
    {
        var instruction = CommitMessagePrompt.Build(diff);

        if (preset.Style == PromptStyle.Gemma)
        {
            // Gemma 3 has no separate system role; the instruction rides in the user turn.
            return "<start_of_turn>user\n" + instruction + "<end_of_turn>\n<start_of_turn>model\n";
        }

        // Hybrid Qwen3 models default to a slow <think> pass; /no_think opts out for snappy output.
        var toggle = preset.HybridThinking ? " /no_think" : string.Empty;
        return
            "<|im_start|>system\nYou are a concise assistant that writes git commit messages.<|im_end|>\n" +
            "<|im_start|>user\n" + instruction + toggle + "<|im_end|>\n" +
            "<|im_start|>assistant\n";
    }

    private async Task EnsureLoadedAsync(string modelPath, CancellationToken cancellationToken)
    {
        if (_executor is not null && string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_executor is not null && string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            DisposeModel();

            var parameters = new ModelParams(modelPath)
            {
                ContextSize = 4096,   // prompt caps at ~8k chars of diff, comfortably within this
                GpuLayerCount = 0,    // CPU backend
            };

            try
            {
                _weights = await LLamaWeights.LoadFromFileAsync(parameters, cancellationToken).ConfigureAwait(false);
                _executor = new StatelessExecutor(_weights, parameters);
                _loadedModelPath = modelPath;
            }
            catch (Exception ex) when (ex is TypeInitializationException or RuntimeError or DllNotFoundException)
            {
                throw new InvalidOperationException(
                    "The llama runtime failed to start — reinstalling GitFlick should restore its native files.", ex);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException($"Couldn't load the model file: {ex.Message}", ex);
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>(Re)arms the idle unload — the weights are freed after 5 quiet minutes.</summary>
    private void NotifyUse()
    {
        lock (_timerGate)
        {
            _idleTimer ??= new Timer(_ => ReleaseIdleModel(), null, Timeout.Infinite, Timeout.Infinite);
            _idleTimer.Change(IdleUnloadAfter, Timeout.InfiniteTimeSpan);
        }
    }

    // Idle callback: free the weights only if no inference is in flight (skip otherwise — the
    // timer re-arms when that inference finishes), then trim the working set.
    private void ReleaseIdleModel()
    {
        if (!_inferLock.Wait(0))
        {
            return;
        }

        var released = false;
        try
        {
            _loadLock.Wait();
            try
            {
                if (IsModelLoaded)
                {
                    DisposeModel();
                    released = true;
                }
            }
            finally
            {
                _loadLock.Release();
            }
        }
        finally
        {
            _inferLock.Release();
        }

        if (released)
        {
            _ = ProcessMemoryTrimService.RequestIdleTrimAsync("llama-idle");
        }
    }

    private void DisposeModel()
    {
        (_executor as IDisposable)?.Dispose();
        _executor = null;
        _weights?.Dispose();
        _weights = null;
        _loadedModelPath = null;
    }

    public void Dispose()
    {
        lock (_timerGate)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
        }

        // Free the model under the locks so we can't race a still-running inference (rare at
        // shutdown). If one is somehow in flight, skip — process exit reclaims the native memory.
        if (_inferLock.Wait(0))
        {
            try
            {
                _loadLock.Wait();
                try
                {
                    DisposeModel();
                }
                finally
                {
                    _loadLock.Release();
                }
            }
            finally
            {
                _inferLock.Release();
            }
        }

        _loadLock.Dispose();
        _inferLock.Dispose();
    }
}
