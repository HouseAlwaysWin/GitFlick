using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using GitFlick.Models;
using GitFlick.Services;

namespace GitFlick.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IGitService _git;
    private readonly List<RepositoryItem> _pinned = [];

    /// <summary>
    /// Shown when the global hotkey could not be registered (e.g. another app owns it).
    /// Empty means the hotkey is live.
    /// </summary>
    [ObservableProperty]
    public partial string HotkeyStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasHotkeyStatus { get; set; }

    /// <summary>Set when git could not be found at startup (spec §1).</summary>
    [ObservableProperty]
    public partial string GitWarning { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasGitWarning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial RepositoryItem? SelectedRepo { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRepoOpen))]
    [NotifyPropertyChangedFor(nameof(IsPaletteVisible))]
    public partial RepositoryItem? OpenRepo { get; set; }

    /// <summary>The open repository's workspace, or null while the palette is showing.</summary>
    [ObservableProperty]
    public partial WorkspaceViewModel? Workspace { get; set; }

    /// <summary>Repos matching the current search, best match first.</summary>
    public ObservableCollection<RepositoryItem> Repos { get; } = [];

    public bool IsRepoOpen => OpenRepo is not null;

    public bool IsPaletteVisible => OpenRepo is null;

    public bool HasStatusMessage => StatusMessage.Length > 0;

    [ObservableProperty]
    public partial bool HasPinnedRepos { get; set; }

    [ObservableProperty]
    public partial bool HasNoMatches { get; set; }

    /// <summary>Design-time only. The running app always supplies real services.</summary>
    public MainViewModel()
        : this(new SettingsService(), new GitService())
    {
    }

    public MainViewModel(ISettingsService settings, IGitService git)
    {
        _settings = settings;
        _git = git;
        ReloadPinned();
    }

    public void ReportHotkeyFailure(string message)
    {
        HotkeyStatus = message;
        HasHotkeyStatus = true;
    }

    public void ReportGitMissing(string message)
    {
        GitWarning = message;
        HasGitWarning = true;
    }

    /// <summary>
    /// A launcher always comes back to its search box. Called every time the window is summoned.
    /// </summary>
    public void ResetForSummon()
    {
        OpenRepo = null;
        Workspace = null;
        StatusMessage = string.Empty;
        SearchText = string.Empty;
        ApplyFilter();
    }

    public void AddRepository(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        if (!IsGitRepository(full))
        {
            StatusMessage = $"'{Path.GetFileName(full)}' is not a Git repository.";
            return;
        }

        var repos = _settings.Current.PinnedRepos;

        if (repos.Any(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = $"'{Path.GetFileName(full)}' is already pinned.";
            return;
        }

        repos.Add(full);
        _settings.Save();

        StatusMessage = string.Empty;
        SearchText = string.Empty;
        ReloadPinned();
        SelectedRepo = Repos.FirstOrDefault(r => r.Path == full);
    }

    public void RemoveSelected()
    {
        if (SelectedRepo is not { } selected)
        {
            return;
        }

        _settings.Current.PinnedRepos.RemoveAll(
            p => string.Equals(p, selected.Path, StringComparison.OrdinalIgnoreCase));
        _settings.Save();

        StatusMessage = string.Empty;
        ReloadPinned();
    }

    public void OpenSelected()
    {
        if (SelectedRepo is { } selected)
        {
            OpenRepo = selected;
            Workspace = new WorkspaceViewModel(_git, selected);
            _ = Workspace.RefreshAsync();
        }
    }

    public void CloseRepo()
    {
        OpenRepo = null;
        Workspace = null;
    }

    /// <summary>Moves the highlight by <paramref name="delta"/>, clamped to the list.</summary>
    public void MoveSelection(int delta)
    {
        if (Repos.Count == 0)
        {
            return;
        }

        var index = SelectedRepo is null ? -1 : Repos.IndexOf(SelectedRepo);
        var next = Math.Clamp(index + delta, 0, Repos.Count - 1);

        SelectedRepo = Repos[next];
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>A worktree or submodule has .git as a file, not a directory. Accept both.</summary>
    private static bool IsGitRepository(string path)
    {
        var dotGit = Path.Combine(path, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }

    private void ReloadPinned()
    {
        _pinned.Clear();

        foreach (var path in _settings.Current.PinnedRepos)
        {
            _pinned.Add(new RepositoryItem(Path.GetFileName(path), path));
        }

        HasPinnedRepos = _pinned.Count > 0;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var previous = SelectedRepo;

        var matches = query.Length == 0
            ? _pinned.AsEnumerable()
            : _pinned
                .Select(repo => (repo, score: Score(repo, query)))
                .Where(x => x.score > int.MinValue)
                .OrderByDescending(x => x.score)
                .Select(x => x.repo);

        Repos.Clear();

        foreach (var repo in matches)
        {
            Repos.Add(repo);
        }

        HasNoMatches = HasPinnedRepos && Repos.Count == 0;
        SelectedRepo = Repos.Contains(previous!) ? previous : Repos.FirstOrDefault();
    }

    /// <summary>Name matches beat path matches, so typing "flick" ranks the repo above its parent folder.</summary>
    private static int Score(RepositoryItem repo, string query)
    {
        if (FuzzyMatcher.TryMatch(repo.Name, query, out var nameScore))
        {
            return nameScore + 100;
        }

        if (FuzzyMatcher.TryMatch(repo.Path, query, out var pathScore))
        {
            return pathScore;
        }

        return int.MinValue;
    }
}
