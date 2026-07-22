using System.Collections.Generic;

namespace GitFlick.Models;

public sealed class AppSettings
{
    /// <summary>Combo that summons the launcher from anywhere. Defaults to Ctrl+Alt+G.</summary>
    public HotkeyDefinition Hotkey { get; set; } = new();

    /// <summary>Reserved for module ② (pinned repos). Unused in module ①.</summary>
    public List<string> PinnedRepos { get; set; } = [];

    /// <summary>How many times each repo (by path) has been opened. Drives the "frequent projects" panel.</summary>
    public Dictionary<string, int> RepoOpenCounts { get; set; } = [];

    /// <summary>Reserved for module ③: explicit git path when it isn't on PATH.</summary>
    public string? GitExecutablePath { get; set; }

    /// <summary>Pre-filled into the commit box for a fresh commit. Null/empty means none.</summary>
    public string? CommitTemplate { get; set; }

    /// <summary>Which engine ✨ Generate uses: the built-in in-process model, or an Ollama server.</summary>
    public CommitAiEngine AiEngine { get; set; } = CommitAiEngine.Builtin;

    /// <summary>Catalog id of the built-in GGUF model (see CommitModelCatalog).</summary>
    public string BuiltinModelId { get; set; } = "qwen3.5-2b";

    /// <summary>Base URL of the local Ollama server used to generate commit messages.</summary>
    public string OllamaUrl { get; set; } = "http://localhost:11434";

    /// <summary>Ollama model name for commit-message generation (must be pulled locally).</summary>
    public string OllamaModel { get; set; } = "llama3.2";

    /// <summary>UI language. Serialized as a string via the source-gen context's enum converter.</summary>
    public Language Language { get; set; } = Language.English;

    /// <summary>Light / Dark / follow-system theme.</summary>
    public AppThemeVariant ThemeVariant { get; set; } = AppThemeVariant.System;

    /// <summary>Accent colour hex (drives the primary buttons and selection highlight).</summary>
    public string AccentColorHex { get; set; } = "#588CF0";

    /// <summary>Check GitHub for a newer release on launch (silent). Off by default.</summary>
    public bool AutoCheckUpdates { get; set; }

    /// <summary>Show a commit's files as a folder tree instead of a flat list of full paths.</summary>
    public bool CommitFilesAsTree { get; set; }
}

/// <summary>How commit messages are generated.</summary>
public enum CommitAiEngine
{
    /// <summary>LLamaSharp running a downloaded GGUF model inside the app — no extra installs.</summary>
    Builtin,

    /// <summary>An external Ollama server (for users who already run one).</summary>
    Ollama,
}

/// <summary>UI languages GitFlick ships translations for.</summary>
public enum Language
{
    English,
    TraditionalChinese,
    SimplifiedChinese,
    Japanese,
}

/// <summary>Theme variant selection.</summary>
public enum AppThemeVariant
{
    System,
    Light,
    Dark,
}
