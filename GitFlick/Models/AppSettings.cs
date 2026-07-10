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
}
