using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitFlick.Models;
using GitFlick.Services;

namespace GitFlick.ViewModels;

/// <summary>
/// The per-repository workspace: status split into staged/unstaged, the commit box, and the
/// branch/remote/stash operations. Every git call funnels through <see cref="RunAsync"/> so
/// busy-state, error feedback and a follow-up refresh are handled in one place.
/// </summary>
public partial class WorkspaceViewModel : ViewModelBase
{
    private readonly IGitService _git;

    public WorkspaceViewModel(IGitService git, RepositoryItem repository)
    {
        _git = git;
        Repository = repository;
    }

    public RepositoryItem Repository { get; }

    public ObservableCollection<GitStatusEntry> UnstagedFiles { get; } = [];

    public ObservableCollection<GitStatusEntry> StagedFiles { get; } = [];

    public ObservableCollection<GitBranch> Branches { get; } = [];

    public ObservableCollection<StashEntry> Stashes { get; } = [];

    [ObservableProperty]
    public partial string BranchName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Upstream { get; set; }

    [ObservableProperty]
    public partial int Ahead { get; set; }

    [ObservableProperty]
    public partial int Behind { get; set; }

    [ObservableProperty]
    public partial GitStatusEntry? SelectedUnstagedFile { get; set; }

    [ObservableProperty]
    public partial GitStatusEntry? SelectedStagedFile { get; set; }

    [ObservableProperty]
    public partial GitBranch? SelectedBranch { get; set; }

    [ObservableProperty]
    public partial string NewBranchName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    public partial string CommitMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    public partial bool HasStagedFiles { get; set; }

    [ObservableProperty]
    public partial bool HasUnstagedFiles { get; set; }

    [ObservableProperty]
    public partial bool HasStashes { get; set; }

    /// <summary>True for a repo with no pending changes, so the UI can say so plainly.</summary>
    [ObservableProperty]
    public partial bool IsCleanTree { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>The unified diff of the selected file, shown in the diff viewer.</summary>
    [ObservableProperty]
    public partial string DiffText { get; set; } = string.Empty;

    /// <summary>Path of the file the diff belongs to; empty when nothing is selected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiff))]
    public partial string DiffPath { get; set; } = string.Empty;

    public bool HasDiff => DiffPath.Length > 0;

    public bool CanCommit =>
        !IsBusy && HasStagedFiles && !string.IsNullOrWhiteSpace(CommitMessage);

    /// <summary>Row height of the commit list. The graph is drawn in row units, so it must match.</summary>
    public const double CommitRowHeight = 26;

    /// <summary>Bound so a huge history can't stall the UI (spec §5⑦ Level 2: keep it bounded).</summary>
    private const int MaxCommits = 300;

    public ObservableCollection<CommitInfo> Commits { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiffEmptyHint))]
    [NotifyPropertyChangedFor(nameof(FooterHint))]
    public partial bool IsHistoryMode { get; set; }

    public string DiffEmptyHint => IsHistoryMode
        ? "Select a commit to see what it changed."
        : "Select a changed file to see its diff.";

    public string FooterHint => IsHistoryMode
        ? "Click a commit to see its changes · Esc back to the palette"
        : "Double-click a file to stage/unstage · Esc back to the palette";

    [ObservableProperty]
    public partial Models.CommitGraph? Graph { get; set; }

    /// <summary>Pixels of graph on the left of each commit row, so the text clears the lanes.</summary>
    [ObservableProperty]
    public partial Thickness CommitListPadding { get; set; } = new(0);

    [ObservableProperty]
    public partial double GraphWidth { get; set; }

    [ObservableProperty]
    public partial CommitInfo? SelectedCommit { get; set; }

    /// <summary>Collapses merges to one row each: "what actually landed on this branch".</summary>
    [ObservableProperty]
    public partial bool FirstParentOnly { get; set; }

    [ObservableProperty]
    public partial bool HasCommits { get; set; }

    /// <summary>The in-flight diff load. Exposed so callers and tests can await it.</summary>
    public Task DiffLoad { get; private set; } = Task.CompletedTask;

    /// <summary>The in-flight history load. Exposed so callers and tests can await it.</summary>
    public Task HistoryLoad { get; private set; } = Task.CompletedTask;

    /// <summary>Reloads the history and rebuilds the lane graph. Never throws.</summary>
    public async Task LoadHistoryAsync()
    {
        try
        {
            var commits = await _git.GetCommitsAsync(
                Repository.Path, MaxCommits, FirstParentOnly);

            Replace(Commits, commits);
            HasCommits = Commits.Count > 0;

            var graph = CommitGraphBuilder.Build(commits, FirstParentOnly);
            Graph = graph;
            GraphWidth = graph.Width;
            CommitListPadding = new Thickness(graph.Width + 6, 0, 0, 0);

            SelectedCommit = null;
            ClearDiff();
        }
        catch (GitException ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private void ShowChanges()
    {
        IsHistoryMode = false;
        ClearDiff();
    }

    [RelayCommand]
    private Task ShowHistory()
    {
        IsHistoryMode = true;
        HistoryLoad = LoadHistoryAsync();
        return HistoryLoad;
    }

    partial void OnFirstParentOnlyChanged(bool value)
    {
        if (IsHistoryMode)
        {
            HistoryLoad = LoadHistoryAsync();
        }
    }

    [RelayCommand]
    private Task CheckoutCommit()
    {
        if (SelectedCommit is not { } commit)
        {
            return Task.CompletedTask;
        }

        // Checking out a bare SHA detaches HEAD, which is rarely what you meant. If a local
        // branch sits on this commit, check that out instead.
        var target = commit.Refs.FirstOrDefault(r => r.Kind == GitRefKind.LocalBranch)?.Name ?? commit.Sha;

        return RunAsync(
            () => _git.CheckoutAsync(Repository.Path, target),
            $"Checked out {target}");
    }

    [RelayCommand]
    private Task CherryPick()
    {
        if (SelectedCommit is not { } commit)
        {
            return Task.CompletedTask;
        }

        return RunAsync(
            () => _git.CherryPickAsync(Repository.Path, commit.Sha),
            $"Cherry-picked {commit.ShortSha}");
    }

    partial void OnSelectedCommitChanged(CommitInfo? value)
    {
        if (value is null)
        {
            return;
        }

        DiffLoad = ShowCommitDiffAsync(value);
    }

    private async Task ShowCommitDiffAsync(CommitInfo commit)
    {
        DiffPath = $"{commit.ShortSha}  {commit.Subject}";
        DiffText = "Loading…";

        try
        {
            var diff = await _git.GetCommitDiffAsync(Repository.Path, commit.Sha);

            DiffText = diff.Trim().Length == 0
                ? "(no textual changes)"
                : diff;
        }
        catch (GitException ex)
        {
            DiffText = ex.Message;
        }
    }

    /// <summary>Reloads status, branches and stashes from git. Never throws.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var status = await _git.GetStatusAsync(Repository.Path);

            BranchName = status.IsDetached ? "(detached)" : status.BranchName ?? string.Empty;
            Upstream = status.Upstream;
            Ahead = status.Ahead;
            Behind = status.Behind;

            // The lists are about to be rebuilt, so any showing diff is about to be stale.
            SelectedUnstagedFile = null;
            SelectedStagedFile = null;
            ClearDiff();

            Replace(UnstagedFiles, status.Unstaged);
            Replace(StagedFiles, status.Staged);
            HasStagedFiles = StagedFiles.Count > 0;
            HasUnstagedFiles = UnstagedFiles.Count > 0;
            IsCleanTree = !HasStagedFiles && !HasUnstagedFiles;

            var branchName = SelectedBranch?.Name;
            var branches = await _git.GetBranchesAsync(Repository.Path);
            Replace(Branches, branches);
            SelectedBranch = Branches.FirstOrDefault(b => b.Name == branchName)
                ?? Branches.FirstOrDefault(b => b.IsCurrent);

            var stashes = await _git.GetStashesAsync(Repository.Path);
            Replace(Stashes, stashes);
            HasStashes = Stashes.Count > 0;
        }
        catch (GitException ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private Task Refresh() => RefreshAsync();

    [RelayCommand]
    private Task Stage(GitStatusEntry? entry)
    {
        entry ??= SelectedUnstagedFile;
        return entry is null
            ? Task.CompletedTask
            : RunAsync(() => _git.StageAsync(Repository.Path, entry.Path), $"Staged {entry.Path}");
    }

    [RelayCommand]
    private Task Unstage(GitStatusEntry? entry)
    {
        entry ??= SelectedStagedFile;
        return entry is null
            ? Task.CompletedTask
            : RunAsync(() => _git.UnstageAsync(Repository.Path, entry.Path), $"Unstaged {entry.Path}");
    }

    [RelayCommand]
    private Task StageAll() => RunAsync(() => _git.StageAllAsync(Repository.Path), "Staged all changes");

    [RelayCommand]
    private Task UnstageAll() => RunAsync(() => _git.UnstageAllAsync(Repository.Path), "Unstaged everything");

    [RelayCommand]
    private async Task Commit()
    {
        if (!CanCommit)
        {
            return;
        }

        var message = CommitMessage;
        await RunAsync(() => _git.CommitAsync(Repository.Path, message), "Committed");

        // Only clear the box on success (a failed commit keeps the message for the retry).
        if (StatusText == "Committed")
        {
            CommitMessage = string.Empty;
        }
    }

    [RelayCommand]
    private Task Fetch() =>
        RunAsync(() => _git.FetchAsync(Repository.Path, Progress()), "Fetched");

    [RelayCommand]
    private Task Pull() =>
        RunAsync(() => _git.PullAsync(Repository.Path, Progress()), "Pulled");

    [RelayCommand]
    private Task Push() =>
        RunAsync(() => _git.PushAsync(Repository.Path, Progress()), "Pushed");

    [RelayCommand]
    private async Task CreateBranch()
    {
        var name = NewBranchName.Trim();
        if (name.Length == 0)
        {
            return;
        }

        await RunAsync(() => _git.CreateBranchAsync(Repository.Path, name), $"Created and switched to {name}");
        NewBranchName = string.Empty;
    }

    [RelayCommand]
    private Task Checkout()
    {
        if (SelectedBranch is not { IsCurrent: false } branch)
        {
            return Task.CompletedTask;
        }

        return RunAsync(() => _git.CheckoutAsync(Repository.Path, branch.Name), $"Switched to {branch.Name}");
    }

    [RelayCommand]
    private Task DeleteBranch()
    {
        if (SelectedBranch is not { IsCurrent: false } branch)
        {
            return Task.CompletedTask;
        }

        return RunAsync(() => _git.DeleteBranchAsync(Repository.Path, branch.Name), $"Deleted {branch.Name}");
    }

    [RelayCommand]
    private Task Merge()
    {
        if (SelectedBranch is not { IsCurrent: false } branch)
        {
            return Task.CompletedTask;
        }

        return RunAsync(() => _git.MergeAsync(Repository.Path, branch.Name), $"Merged {branch.Name}");
    }

    [RelayCommand]
    private Task StashPush() => RunAsync(() => _git.StashPushAsync(Repository.Path), "Stashed changes");

    [RelayCommand]
    private Task StashPop()
    {
        if (Stashes.Count == 0)
        {
            return Task.CompletedTask;
        }

        return RunAsync(() => _git.StashPopAsync(Repository.Path), "Popped stash");
    }

    // Selecting in one list clears the other, so the diff always corresponds to exactly one file.
    partial void OnSelectedUnstagedFileChanged(GitStatusEntry? value)
    {
        if (value is null)
        {
            return;
        }

        SelectedStagedFile = null;
        DiffLoad = ShowDiffAsync(value, staged: false);
    }

    partial void OnSelectedStagedFileChanged(GitStatusEntry? value)
    {
        if (value is null)
        {
            return;
        }

        SelectedUnstagedFile = null;
        DiffLoad = ShowDiffAsync(value, staged: true);
    }

    private async Task ShowDiffAsync(GitStatusEntry entry, bool staged)
    {
        DiffPath = entry.Path;
        DiffText = "Loading…";

        try
        {
            var diff = await _git.GetDiffAsync(
                Repository.Path,
                entry.Path,
                staged,
                untracked: entry.Kind == GitChangeKind.Untracked);

            DiffText = diff.Trim().Length == 0
                ? "(no textual changes)"
                : diff;
        }
        catch (GitException ex)
        {
            DiffText = ex.Message;
        }
    }

    private void ClearDiff()
    {
        DiffPath = string.Empty;
        DiffText = string.Empty;
    }

    private IProgress<string> Progress() => new Progress<string>(line => StatusText = line);

    /// <summary>
    /// Runs one git operation with busy-state and error handling, then refreshes. Sets
    /// <see cref="StatusText"/> to the success message or git's own failure text.
    /// </summary>
    private async Task RunAsync(Func<Task<GitCommandResult>> operation, string successMessage)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "Working…";

        try
        {
            var result = await operation();
            StatusText = result.Succeeded ? successMessage : result.FailureMessage;
        }
        catch (GitException ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();

            // Checkout, cherry-pick, merge and friends move HEAD, so the graph is stale too.
            if (IsHistoryMode)
            {
                await LoadHistoryAsync();
            }
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, System.Collections.Generic.IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
