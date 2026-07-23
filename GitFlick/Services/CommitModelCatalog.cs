using System;
using System.Collections.Generic;
using System.IO;
using GitFlick.Services.Updates;

namespace GitFlick.Services;

/// <summary>Which chat template the model was trained on.</summary>
public enum PromptStyle
{
    /// <summary>ChatML (&lt;|im_start|&gt; / &lt;|im_end|&gt;) — Qwen 2.5 and Qwen 3.</summary>
    ChatML,

    /// <summary>Gemma turns (&lt;start_of_turn&gt; / &lt;end_of_turn&gt;).</summary>
    Gemma,
}

/// <summary>One downloadable GGUF preset for the built-in commit-message engine.</summary>
public sealed record CommitModelPreset(
    string Id,
    string DisplayName,
    string Url,
    string FileName,
    string Sha256,
    long Size,
    PromptStyle Style = PromptStyle.ChatML,
    bool HybridThinking = false)
{
    public string SizeDisplay => $"{Size / 1024.0 / 1024.0 / 1024.0:0.0} GB";
}

/// <summary>
/// The built-in engine's model catalog (mirrors GimmeCapture's AIModelCatalog, scaled down).
/// Qwen3.5 2B won the commit-message shootout (accurate, clean Conventional Commits, ~4-5 s on
/// CPU); the rest cover code-tuned, higher-quality, multilingual, and fastest niches.
/// </summary>
public static class CommitModelCatalog
{
    public const string DefaultModelId = "qwen3.5-2b";

    public static readonly IReadOnlyList<CommitModelPreset> Presets =
    [
        new(
            "qwen3.5-2b",
            "Qwen3.5 2B (1.2 GB) — recommended",
            "https://huggingface.co/unsloth/Qwen3.5-2B-GGUF/resolve/main/Qwen3.5-2B-Q4_K_M.gguf?download=true",
            "Qwen3.5-2B-Q4_K_M.gguf",
            "aaf42c8b7c3cab2bf3d69c355048d4a0ee9973d48f16c731c0520ee914699223",
            1_280_835_840,
            HybridThinking: true),   // thinking is suppressed with /no_think for snappy output
        new(
            "qwen3-1.7b",
            "Qwen3 1.7B (1.3 GB)",
            "https://huggingface.co/bartowski/Qwen_Qwen3-1.7B-GGUF/resolve/main/Qwen_Qwen3-1.7B-Q4_K_M.gguf?download=true",
            "Qwen_Qwen3-1.7B-Q4_K_M.gguf",
            "72c5c3cb38fa32d5256e2fe30d03e7a64c6c79e668ad84057e3bd66e250b24fb",
            1_282_439_584,
            HybridThinking: true),
        new(
            "qwen2.5-coder-1.5b",
            "Qwen2.5 Coder 1.5B (1.0 GB) — code-tuned",
            "https://huggingface.co/bartowski/Qwen2.5-Coder-1.5B-Instruct-GGUF/resolve/main/Qwen2.5-Coder-1.5B-Instruct-Q4_K_M.gguf?download=true",
            "Qwen2.5-Coder-1.5B-Instruct-Q4_K_M.gguf",
            "f530705d447660a4336c329981af164b471b60b974b1d808d57e8ec9fe23b239",
            986_048_800),
        new(
            "qwen3.5-4b",
            "Qwen3.5 4B (2.6 GB) — best quality",
            "https://huggingface.co/unsloth/Qwen3.5-4B-GGUF/resolve/main/Qwen3.5-4B-Q4_K_M.gguf?download=true",
            "Qwen3.5-4B-Q4_K_M.gguf",
            "00fe7986ff5f6b463e62455821146049db6f9313603938a70800d1fb69ef11a4",
            2_740_937_888,
            HybridThinking: true),
        new(
            "gemma-4-e2b",
            "Gemma 4 E2B (3.2 GB) — multilingual",
            "https://huggingface.co/lmstudio-community/gemma-4-E2B-it-GGUF/resolve/main/gemma-4-E2B-it-Q4_K_M.gguf?download=true",
            "gemma-4-E2B-it-Q4_K_M.gguf",
            "6c950d754366dd8b372fd17a40497ba5f130a46d833b4c5bccc9f6bb6382ce1e",
            3_427_877_696,
            PromptStyle.Gemma),
        new(
            "qwen2.5-0.5b",
            "Qwen2.5 0.5B (0.4 GB) — fastest, basic",
            "https://huggingface.co/bartowski/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/Qwen2.5-0.5B-Instruct-Q4_K_M.gguf?download=true",
            "Qwen2.5-0.5B-Instruct-Q4_K_M.gguf",
            "6eb923e7d26e9cea28811e1a8e852009b21242fb157b26149d3b188f3a8c8653",
            397_808_192),
    ];

    /// <summary>Where downloaded models live: %APPDATA%/GitFlick/models.</summary>
    public static string ModelsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GitFlick",
        "models");

    public static CommitModelPreset Resolve(string? modelId)
    {
        foreach (var preset in Presets)
        {
            if (string.Equals(preset.Id, modelId, StringComparison.Ordinal))
            {
                return preset;
            }
        }

        return Presets[0];   // unknown/legacy id falls back to the default
    }

    public static string GetModelPath(CommitModelPreset preset) =>
        Path.Combine(ModelsDirectory, preset.FileName);

    /// <summary>The download descriptor for a preset — what ArtifactDownloader needs to fetch and
    /// verify it (URL, filename, SHA-256, exact byte size). Lands the file at <see cref="GetModelPath"/>.</summary>
    public static ArtifactDescriptor DescriptorFor(CommitModelPreset preset) =>
        new(new Uri(preset.Url), preset.FileName, preset.Sha256, preset.Size);

    public static bool IsDownloaded(CommitModelPreset preset) =>
        File.Exists(GetModelPath(preset));
}
