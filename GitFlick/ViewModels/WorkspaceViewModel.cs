using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
/// Which column the history is ordered by. <see cref="Graph"/> is git's own topological
/// (<c>--date-order</c>) order — the only one the lane graph can be drawn against, so any other
/// choice hides the graph. Only Author and Date are user-sortable.
/// </summary>
public enum HistorySortColumn
{
    Graph,
    Author,
    Date,
}

/// <summary>One tickable option in a history multi-select filter (an author or a branch).
/// Toggling <see cref="IsSelected"/> re-filters the list.</summary>
public partial class FilterOption : ObservableObject
{
    public FilterOption(string name) => Name = name;

    public string Name { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

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
        : "Double-click, or select multiple (Ctrl/Shift) and press Enter, to stage/unstage · Esc back";

    [ObservableProperty]
    public partial Models.CommitGraph? Graph { get; set; }

    /// <summary>Pixels of graph on the left of each commit row, so the text clears the lanes.</summary>
    [ObservableProperty]
    public partial Thickness CommitListPadding { get; set; } = new(0);

    [ObservableProperty]
    public partial double GraphWidth { get; set; }

    [ObservableProperty]
    public partial CommitInfo? SelectedCommit { get; set; }

    /// <summary>The files the selected commit changed; picking one shows just that file's diff.</summary>
    public ObservableCollection<CommitFileEntry> CommitFiles { get; } = [];

    [ObservableProperty]
    public partial CommitFileEntry? SelectedCommitFile { get; set; }

    [ObservableProperty]
    public partial bool HasCommitFiles { get; set; }

    /// <summary>SHA whose file list is showing, so a file diff loads against the right commit.</summary>
    private string? _selectedCommitSha;

    /// <summary>Collapses merges to one row each: "what actually landed on this branch".</summary>
    [ObservableProperty]
    public partial bool FirstParentOnly { get; set; }

    [ObservableProperty]
    public partial bool HasCommits { get; set; }

    /// <summary>Which column the list is ordered by. Anything but <see cref="HistorySortColumn.Graph"/> hides the graph.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGraph))]
    [NotifyPropertyChangedFor(nameof(AuthorSortGlyph))]
    [NotifyPropertyChangedFor(nameof(DateSortGlyph))]
    public partial HistorySortColumn SortColumn { get; set; } = HistorySortColumn.Graph;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AuthorSortGlyph))]
    [NotifyPropertyChangedFor(nameof(DateSortGlyph))]
    public partial bool SortDescending { get; set; }

    /// <summary>
    /// Whether the lane graph shows. It needs git's order (no column sort) and a parent-closed set.
    /// A branch filter keeps a valid sub-DAG (all ancestors of the tips), so the graph is rebuilt
    /// for it; an author filter does not (an author's commits link through other people's), so it
    /// stays hidden there.
    /// </summary>
    public bool ShowGraph => SortColumn == HistorySortColumn.Graph && !HasAuthorFilter;

    // The active column wears an arrow; the rest show nothing.
    public string AuthorSortGlyph => GlyphFor(HistorySortColumn.Author);
    public string DateSortGlyph => GlyphFor(HistorySortColumn.Date);

    private string GlyphFor(HistorySortColumn column) =>
        SortColumn == column ? (SortDescending ? " ▼" : " ▲") : string.Empty;

    /// <summary>Distinct authors in the loaded history; ticking any narrows the list to those.</summary>
    public ObservableCollection<FilterOption> AuthorFilters { get; } = [];

    /// <summary>The authors matching <see cref="AuthorFilterSearch"/> — what the flyout actually shows.</summary>
    public ObservableCollection<FilterOption> FilteredAuthorFilters { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGraph))]
    [NotifyPropertyChangedFor(nameof(AuthorFilterLabel))]
    public partial bool HasAuthorFilter { get; set; }

    public string AuthorFilterLabel =>
        HasAuthorFilter ? $"Authors ({AuthorFilters.Count(a => a.IsSelected)}) ▾" : "Authors ▾";

    /// <summary>Fuzzy query that narrows the author checklist so a long list isn't a scroll marathon.</summary>
    [ObservableProperty]
    public partial string AuthorFilterSearch { get; set; } = string.Empty;

    partial void OnAuthorFilterSearchChanged(string value) =>
        Narrow(AuthorFilters, FilteredAuthorFilters, value);

    /// <summary>Distinct branches/remotes in the loaded history; ticking narrows to their commits.</summary>
    public ObservableCollection<FilterOption> BranchFilters { get; } = [];

    /// <summary>The branches matching <see cref="BranchFilterSearch"/> — what the flyout actually shows.</summary>
    public ObservableCollection<FilterOption> FilteredBranchFilters { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGraph))]
    [NotifyPropertyChangedFor(nameof(BranchFilterLabel))]
    public partial bool HasBranchFilter { get; set; }

    public string BranchFilterLabel =>
        HasBranchFilter ? $"Branches ({BranchFilters.Count(b => b.IsSelected)}) ▾" : "Branches ▾";

    [ObservableProperty]
    public partial string BranchFilterSearch { get; set; } = string.Empty;

    partial void OnBranchFilterSearchChanged(string value) =>
        Narrow(BranchFilters, FilteredBranchFilters, value);

    /// <summary>Suppresses per-item re-filtering while several ticks change at once.</summary>
    private bool _suppressFilterApply;

    // Column widths for the History table, shared by the header and every row so a drag on one
    // splitter resizes the whole column. Message takes the remaining space, so it isn't listed.
    [ObservableProperty]
    public partial GridLength AuthorColumnWidth { get; set; } = new(92);

    [ObservableProperty]
    public partial GridLength DateColumnWidth { get; set; } = new(110);

    [ObservableProperty]
    public partial GridLength CommitColumnWidth { get; set; } = new(66);

    /// <summary>
    /// Left/right inset on the header row so its columns line up with the list rows despite the
    /// graph gutter (left) and the item padding + scrollbar (right). Tracks <see cref="ShowGraph"/>.
    /// </summary>
    [ObservableProperty]
    public partial Thickness HistoryHeaderMargin { get; set; } = new(4, 0, HistoryRightInset, 0);

    /// <summary>Reserves the right edge for the item padding and the overlay scrollbar.</summary>
    private const double HistoryRightInset = 14;

    /// <summary>History as git returned it — the order the graph was built for. Restored on reset.</summary>
    private List<CommitInfo> _graphOrder = [];

    /// <summary>Suppresses the diff reload while the list is being re-sorted under a kept selection.</summary>
    private bool _reorderingCommits;

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

            _graphOrder = commits.ToList();

            RebuildAuthorFilters();
            RebuildBranchFilters();

            // A fresh load always starts in git's order so the lane graph lines up.
            SortColumn = HistorySortColumn.Graph;
            SortDescending = false;

            // ApplyView builds the lane graph for the (possibly filtered) subset it shows.
            ApplyView();
            HasCommits = Commits.Count > 0;

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

    /// <summary>
    /// Header click: <see cref="HistorySortColumn.Graph"/> restores git's order; Author/Date sort
    /// by that column, toggling ascending/descending when it is already the active column.
    /// </summary>
    [RelayCommand]
    private void SortBy(HistorySortColumn column)
    {
        if (column == HistorySortColumn.Graph)
        {
            SortColumn = HistorySortColumn.Graph;
            SortDescending = false;
        }
        else if (SortColumn == column)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;
            SortDescending = false;
        }

        ApplyView();
    }

    /// <summary>Drops the column sort AND both filters, returning to git's order (graph shown again).</summary>
    [RelayCommand]
    private void ResetSort()
    {
        _suppressFilterApply = true;
        foreach (var item in AuthorFilters)
        {
            item.IsSelected = false;
        }
        foreach (var item in BranchFilters)
        {
            item.IsSelected = false;
        }
        _suppressFilterApply = false;

        SortColumn = HistorySortColumn.Graph;
        SortDescending = false;
        ApplyView();
        OnPropertyChanged(nameof(AuthorFilterLabel));
        OnPropertyChanged(nameof(BranchFilterLabel));
    }

    /// <summary>Clears every author tick in one shot, re-filtering once.</summary>
    [RelayCommand]
    private void ClearAuthorFilter() => ClearFilter(AuthorFilters, nameof(AuthorFilterLabel));

    /// <summary>Clears every branch tick in one shot, re-filtering once.</summary>
    [RelayCommand]
    private void ClearBranchFilter() => ClearFilter(BranchFilters, nameof(BranchFilterLabel));

    private void ClearFilter(ObservableCollection<FilterOption> options, string labelProperty)
    {
        _suppressFilterApply = true;
        foreach (var item in options)
        {
            item.IsSelected = false;
        }
        _suppressFilterApply = false;

        ApplyView();
        OnPropertyChanged(labelProperty);
    }

    /// <summary>Applies the branch filter, then the author filter, then the sort; repositions the gutter.</summary>
    private void ApplyView()
    {
        var authors = AuthorFilters.Where(a => a.IsSelected).Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        var branches = BranchFilters.Where(b => b.IsSelected).Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        HasAuthorFilter = authors.Count > 0;
        HasBranchFilter = branches.Count > 0;

        // Filter in git's order first. A branch filter keeps every ancestor of the tips, so the
        // result is still a parent-closed sub-DAG the graph can be drawn against.
        IEnumerable<CommitInfo> filtered = _graphOrder;

        if (HasBranchFilter)
        {
            var reachable = ReachableFrom(branches);
            filtered = filtered.Where(c => reachable.Contains(c.Sha));
        }

        if (HasAuthorFilter)
        {
            filtered = filtered.Where(c => authors.Contains(c.Author));
        }

        var gitOrder = filtered.ToList();

        // Rebuild the lane graph for the current subset when it will actually be shown — i.e. in
        // git's order with no author filter (an author subset isn't parent-closed, so its lanes
        // would dangle). This is what lets the graph survive a branch filter.
        if (ShowGraph)
        {
            var graph = CommitGraphBuilder.Build(gitOrder, FirstParentOnly);
            Graph = graph;
            GraphWidth = graph.Width;
        }

        IEnumerable<CommitInfo> view = SortColumn switch
        {
            HistorySortColumn.Author => Order(gitOrder, c => c.Author, StringComparer.CurrentCultureIgnoreCase),
            HistorySortColumn.Date => Order(gitOrder, c => c.When, Comparer<DateTimeOffset>.Default),
            _ => gitOrder,   // Graph: keep git's order
        };

        // Keep the selection across the reshuffle without re-triggering the diff load.
        var keep = SelectedCommit;
        _reorderingCommits = true;
        Replace(Commits, view);
        SelectedCommit = keep is not null && Commits.Contains(keep) ? keep : null;
        _reorderingCommits = false;

        // The graph aligns only with git's order, so drop its gutter when it's hidden.
        CommitListPadding = ShowGraph ? new Thickness(GraphWidth + 6, 0, 0, 0) : new Thickness(0);
        HistoryHeaderMargin = new Thickness(ShowGraph ? GraphWidth + 10 : 4, 0, HistoryRightInset, 0);
    }

    // OrderBy/OrderByDescending are stable, so equal keys keep their git order within the group.
    private IEnumerable<CommitInfo> Order<TKey>(IEnumerable<CommitInfo> source, Func<CommitInfo, TKey> key, IComparer<TKey> comparer) =>
        SortDescending
            ? source.OrderByDescending(key, comparer)
            : source.OrderBy(key, comparer);

    /// <summary>SHAs reachable from the tips that carry any of the given branch names — an in-memory
    /// ancestor walk over the loaded history (no extra git call, so it composes with the other filters).</summary>
    private HashSet<string> ReachableFrom(HashSet<string> branchNames)
    {
        var bySha = new Dictionary<string, CommitInfo>(StringComparer.Ordinal);
        foreach (var commit in _graphOrder)
        {
            bySha[commit.Sha] = commit;
        }

        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();

        foreach (var commit in _graphOrder)
        {
            if (commit.Refs.Any(r => branchNames.Contains(r.Name)))
            {
                stack.Push(commit.Sha);
            }
        }

        while (stack.Count > 0)
        {
            var sha = stack.Pop();
            if (!reachable.Add(sha))
            {
                continue;
            }

            if (bySha.TryGetValue(sha, out var commit))
            {
                foreach (var parent in commit.Parents)
                {
                    stack.Push(parent);
                }
            }
        }

        return reachable;
    }

    private void RebuildAuthorFilters()
    {
        RebuildFilterOptions(
            AuthorFilters,
            _graphOrder.Select(c => c.Author).Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase),
            OnAuthorFilterItemChanged);
        Narrow(AuthorFilters, FilteredAuthorFilters, AuthorFilterSearch);
    }

    private void RebuildBranchFilters()
    {
        RebuildFilterOptions(
            BranchFilters,
            _graphOrder.SelectMany(c => c.Refs).Where(r => r.Kind != GitRefKind.Tag).Select(r => r.Name)
                .Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase),
            OnBranchFilterItemChanged);
        Narrow(BranchFilters, FilteredBranchFilters, BranchFilterSearch);
    }

    /// <summary>Rebuilds a checklist from the loaded history, preserving prior ticks by name.</summary>
    private static void RebuildFilterOptions(
        ObservableCollection<FilterOption> options, IEnumerable<string> names, PropertyChangedEventHandler onChanged)
    {
        var wasChosen = options.Where(o => o.IsSelected).Select(o => o.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var item in options)
        {
            item.PropertyChanged -= onChanged;
        }

        options.Clear();

        foreach (var name in names)
        {
            // Tick before subscribing so restoring a selection doesn't re-enter ApplyView mid-build.
            var item = new FilterOption(name) { IsSelected = wasChosen.Contains(name) };
            item.PropertyChanged += onChanged;
            options.Add(item);
        }
    }

    /// <summary>Copies into <paramref name="shown"/> the options whose name fuzzy-matches the query.</summary>
    private static void Narrow(
        ObservableCollection<FilterOption> all, ObservableCollection<FilterOption> shown, string query)
    {
        query = query.Trim();
        shown.Clear();

        foreach (var option in all)
        {
            if (query.Length == 0 || FuzzyMatcher.TryMatch(option.Name, query, out _))
            {
                shown.Add(option);
            }
        }
    }

    private void OnAuthorFilterItemChanged(object? sender, PropertyChangedEventArgs e) =>
        OnFilterItemChanged(e, nameof(AuthorFilterLabel));

    private void OnBranchFilterItemChanged(object? sender, PropertyChangedEventArgs e) =>
        OnFilterItemChanged(e, nameof(BranchFilterLabel));

    private void OnFilterItemChanged(PropertyChangedEventArgs e, string labelProperty)
    {
        if (_suppressFilterApply || e.PropertyName != nameof(FilterOption.IsSelected))
        {
            return;
        }

        ApplyView();
        OnPropertyChanged(labelProperty);   // the "(N)" count moved
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

    /// <summary>
    /// Checks out a branch badge double-clicked in the graph (like Git Graph). A remote branch
    /// DWIMs to its local tracking branch (git creates it if needed); a tag is ignored, since
    /// checking one out would only detach HEAD.
    /// </summary>
    public Task CheckoutRef(GitRef reference)
    {
        if (reference.Kind == GitRefKind.Tag)
        {
            return Task.CompletedTask;
        }

        // "origin/main" -> "main": git checks out (or creates) the local branch tracking it.
        var target = reference.Kind == GitRefKind.RemoteBranch
            ? reference.Name[(reference.Name.IndexOf('/') + 1)..]
            : reference.Name;

        return RunAsync(() => _git.CheckoutAsync(Repository.Path, target), $"Checked out {target}");
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
        if (_reorderingCommits || value is null)
        {
            return;
        }

        DiffLoad = LoadCommitFilesAsync(value);
    }

    /// <summary>Loads the selected commit's changed files, then shows the first file's diff.</summary>
    private async Task LoadCommitFilesAsync(CommitInfo commit)
    {
        _selectedCommitSha = commit.Sha;
        SelectedCommitFile = null;   // null-guarded, so this doesn't fire a diff load
        CommitFiles.Clear();
        HasCommitFiles = false;
        DiffPath = string.Empty;
        DiffText = "Loading…";

        try
        {
            var files = await _git.GetCommitFilesAsync(Repository.Path, commit.Sha);

            // The selection may have moved on while we were awaiting — ignore a stale result.
            if (_selectedCommitSha != commit.Sha)
            {
                return;
            }

            Replace(CommitFiles, files);
            HasCommitFiles = CommitFiles.Count > 0;

            SelectedCommitFile = CommitFiles.FirstOrDefault();   // fires the file diff load
            if (SelectedCommitFile is null)
            {
                DiffText = "(no textual changes)";
            }
        }
        catch (GitException ex)
        {
            DiffText = ex.Message;
        }
    }

    partial void OnSelectedCommitFileChanged(CommitFileEntry? value)
    {
        if (value is null || _selectedCommitSha is null)
        {
            return;
        }

        DiffLoad = ShowCommitFileDiffAsync(_selectedCommitSha, value);
    }

    private async Task ShowCommitFileDiffAsync(string sha, CommitFileEntry file)
    {
        DiffPath = file.Path;
        DiffText = "Loading…";

        try
        {
            var diff = await _git.GetCommitFileDiffAsync(Repository.Path, sha, file.Path);

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

    /// <summary>Stages every file in one batch — the multi-select action from the Unstaged list.</summary>
    public Task StageFiles(IReadOnlyList<GitStatusEntry> files) =>
        files.Count == 0
            ? Task.CompletedTask
            : RunAsync(() => StageOrUnstageAllAsync(files, stage: true),
                files.Count == 1 ? $"Staged {files[0].Path}" : $"Staged {files.Count} files");

    /// <summary>Unstages every file in one batch — the multi-select action from the Staged list.</summary>
    public Task UnstageFiles(IReadOnlyList<GitStatusEntry> files) =>
        files.Count == 0
            ? Task.CompletedTask
            : RunAsync(() => StageOrUnstageAllAsync(files, stage: false),
                files.Count == 1 ? $"Unstaged {files[0].Path}" : $"Unstaged {files.Count} files");

    private async Task<GitCommandResult> StageOrUnstageAllAsync(IReadOnlyList<GitStatusEntry> files, bool stage)
    {
        var result = new GitCommandResult(0, string.Empty, string.Empty);

        foreach (var file in files)
        {
            result = stage
                ? await _git.StageAsync(Repository.Path, file.Path)
                : await _git.UnstageAsync(Repository.Path, file.Path);

            if (!result.Succeeded)
            {
                break;   // stop on the first failure; RunAsync surfaces its message
            }
        }

        return result;
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
        _selectedCommitSha = null;
        SelectedCommitFile = null;
        CommitFiles.Clear();
        HasCommitFiles = false;
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
