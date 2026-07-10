using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GitFlick.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    /// <summary>
    /// Shown when the global hotkey could not be registered (e.g. another app owns it).
    /// Empty means the hotkey is live.
    /// </summary>
    [ObservableProperty]
    public partial string HotkeyStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasHotkeyStatus { get; set; }

    /// <summary>
    /// Scaffolding for the module ① acceptance pass: proves the process is never restarted
    /// and the same window instance is reused. Goes away once real content lands.
    /// </summary>
    [ObservableProperty]
    public partial string SessionInfo { get; set; } = string.Empty;

    private int _showCount;

    public MainViewModel() => UpdateSessionInfo();

    public void NoteShown()
    {
        _showCount++;
        UpdateSessionInfo();
    }

    public void ReportHotkeyFailure(string message)
    {
        HotkeyStatus = message;
        HasHotkeyStatus = true;
    }

    private void UpdateSessionInfo() =>
        SessionInfo = $"PID {Environment.ProcessId} · shown {_showCount}×";
}
