using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
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

    /// <summary>True for a repo with no pending changes, so the UI can say so plainly.</summary>
    [ObservableProperty]
    public partial bool IsCleanTree { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    public bool CanCommit =>
        !IsBusy && HasStagedFiles && !string.IsNullOrWhiteSpace(CommitMessage);

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
