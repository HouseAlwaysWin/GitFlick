using System.Collections.Generic;

namespace GitFlick.Models;

public sealed class AppSettings
{
    /// <summary>Combo that summons the launcher from anywhere. Defaults to Ctrl+Alt+G.</summary>
    public HotkeyDefinition Hotkey { get; set; } = new();

    /// <summary>Reserved for module ② (pinned repos). Unused in module ①.</summary>
    public List<string> PinnedRepos { get; set; } = [];

    /// <summary>Reserved for module ③: explicit git path when it isn't on PATH.</summary>
    public string? GitExecutablePath { get; set; }

    /// <summary>Pre-filled into the commit box for a fresh commit. Null/empty means none.</summary>
    public string? CommitTemplate { get; set; }

    /// <summary>Base URL of the local Ollama server used to generate commit messages.</summary>
    public string OllamaUrl { get; set; } = "http://localhost:11434";

    /// <summary>Ollama model name for commit-message generation (must be pulled locally).</summary>
    public string OllamaModel { get; set; } = "llama3.2";
}
