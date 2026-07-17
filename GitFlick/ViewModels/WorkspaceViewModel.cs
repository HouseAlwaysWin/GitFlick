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
    private readonly ISettingsService? _settings;
    private readonly ICommitMessageGenerator? _ai;

    /// <summary>Shorthand for the app string table (transient status text, resolved in the current language).</summary>
    private static LocalizationService Loc => LocalizationService.Instance;

    public WorkspaceViewModel(
        IGitService git,
        RepositoryItem repository,
        ISettingsService? settings = null,
        ICommitMessageGenerator? ai = null)
    {
        _git = git;
        Repository = repository;
        _settings = settings;
        _ai = ai;
        CommitMessage = TemplateOrEmpty;   // start a fresh commit from the template
    }

    public RepositoryItem Repository { get; }

    /// <summary>The configured commit template, or empty when none is set.</summary>
    private string TemplateOrEmpty =>
        _settings?.Current.CommitTemplate is { Length: > 0 } template ? template : string.Empty;

    /// <summary>Commit template, edited from the ⚙ flyout. Persists immediately.</summary>
    public string CommitTemplate
    {
        get => _settings?.Current.CommitTemplate ?? string.Empty;
        set
        {
            if (_settings is null)
            {
                return;
            }

            _settings.Current.CommitTemplate = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Ollama server URL, edited from the ⚙ flyout. Persists immediately.</summary>
    public string OllamaUrl
    {
        get => _settings?.Current.OllamaUrl ?? string.Empty;
        set
        {
            if (_settings is null)
            {
                return;
            }

            _settings.Current.OllamaUrl = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Ollama model name, edited from the ⚙ flyout. Persists immediately.</summary>
    public string OllamaModel
    {
        get => _settings?.Current.OllamaModel ?? string.Empty;
        set
        {
            if (_settings is null)
            {
                return;
            }

            _settings.Current.OllamaModel = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Engine selector for the ⚙ combo: 0 = built-in model, 1 = Ollama server.</summary>
    public int AiEngineIndex
    {
        get => _settings?.Current.AiEngine == CommitAiEngine.Ollama ? 1 : 0;
        set
        {
            if (_settings is null)
            {
                return;
            }

            _settings.Current.AiEngine = value == 1 ? CommitAiEngine.Ollama : CommitAiEngine.Builtin;
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(UseBuiltinEngine));
            OnPropertyChanged(nameof(UseOllamaEngine));
        }
    }

    public bool UseBuiltinEngine => AiEngineIndex == 0;

    public bool UseOllamaEngine => AiEngineIndex == 1;

    /// <summary>The built-in model presets, for the ⚙ combo.</summary>
    public IReadOnlyList<CommitModelPreset> BuiltinModels => CommitModelCatalog.Presets;

    /// <summary>The selected built-in model preset. Persists immediately; updates the status line.</summary>
    public CommitModelPreset SelectedBuiltinModel
    {
        get => CommitModelCatalog.Resolve(_settings?.Current.BuiltinModelId);
        set
        {
            if (_settings is null || value is null)
            {
                return;
            }

            _settings.Current.BuiltinModelId = value.Id;
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(BuiltinModelStatus));
            OnPropertyChanged(nameof(NeedsModelDownload));
        }
    }

    /// <summary>"Downloaded ✓" or "Not downloaded (n GB)" for the selected built-in model.</summary>
    public string BuiltinModelStatus =>
        CommitModelCatalog.IsDownloaded(SelectedBuiltinModel)
            ? "Downloaded ✓"
            : $"Not downloaded ({SelectedBuiltinModel.SizeDisplay})";

    public bool NeedsModelDownload => !CommitModelCatalog.IsDownloaded(SelectedBuiltinModel) && !IsDownloadingModel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsModelDownload))]
    public partial bool IsDownloadingModel { get; set; }

    [ObservableProperty]
    public partial double ModelDownloadProgress { get; set; }

    /// <summary>Downloads the selected built-in GGUF with SHA-256 verification and live progress.</summary>
    [RelayCommand]
    private async Task DownloadModel()
    {
        if (IsDownloadingModel)
        {
            return;
        }

        var preset = SelectedBuiltinModel;
        if (CommitModelCatalog.IsDownloaded(preset))
        {
            return;
        }

        IsDownloadingModel = true;
        ModelDownloadProgress = 0;
        StatusText = string.Format(Loc["Status_DownloadingModel"], preset.FileName);
        try
        {
            var progress = new Progress<double>(fraction =>
            {
                ModelDownloadProgress = fraction * 100;
            });
            await new ModelDownloader().DownloadAsync(preset, progress);
            StatusText = Loc["Status_ModelDownloaded"];
        }
        catch (Exception ex)
        {
            StatusText = string.Format(Loc["Status_DownloadFailed"], ex.Message);
        }
        finally
        {
            IsDownloadingModel = false;
            OnPropertyChanged(nameof(BuiltinModelStatus));
            OnPropertyChanged(nameof(NeedsModelDownload));
        }
    }

    public ObservableCollection<GitStatusEntry> UnstagedFiles { get; } = [];

    public ObservableCollection<GitStatusEntry> StagedFiles { get; } = [];

    public ObservableCollection<GitBranch> Branches { get; } = [];

    public ObservableCollection<StashEntry> Stashes { get; } = [];

    /// <summary>Snapshot of the git command log, filled when the log flyout opens.</summary>
    public ObservableCollection<GitCommandLogEntry> CommandLog { get; } = [];

    public bool HasCommandLog => CommandLog.Count > 0;

    /// <summary>Pull the newest most-recent-first snapshot of the git command log into the view.</summary>
    public void RefreshCommandLog()
    {
        Replace(CommandLog, _git.CommandLog.Snapshot());
        OnPropertyChanged(nameof(HasCommandLog));
    }

    [RelayCommand]
    private void ClearCommandLog()
    {
        _git.CommandLog.Clear();
        RefreshCommandLog();
    }

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

    /// <summary>History loads in pages so a huge repo can't stall the UI; "Load more" grows the window.</summary>
    private const int CommitPageSize = 300;

    /// <summary>How many commits the current history load asks git for. Grows via "Load more".</summary>
    private int _commitLimit = CommitPageSize;

    /// <summary>True when the last load hit the limit, so there are (probably) older commits to fetch.</summary>
    [ObservableProperty]
    public partial bool HasMoreCommits { get; set; }

    public ObservableCollection<CommitInfo> Commits { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiffEmptyHint))]
    [NotifyPropertyChangedFor(nameof(FooterHint))]
    public partial bool IsHistoryMode { get; set; }

    public string DiffEmptyHint => IsHistoryMode
        ? Loc["Diff_SelectCommit"]
        : Loc["Diff_SelectFile"];

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
                Repository.Path, _commitLimit, FirstParentOnly);

            _graphOrder = commits.ToList();

            // git returns min(limit, total); hitting the limit means older commits are still unfetched.
            HasMoreCommits = commits.Count >= _commitLimit;

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

    /// <summary>Grows the history window by another page and reloads, keeping the selected commit.</summary>
    [RelayCommand]
    private async Task LoadMoreCommits()
    {
        var keepSha = SelectedCommit?.Sha;
        _commitLimit += CommitPageSize;

        await LoadHistoryAsync();

        if (keepSha is not null)
        {
            SelectedCommit = Commits.FirstOrDefault(c => c.Sha == keepSha);
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

    /// <summary>
    /// Set by the View: asks the user to confirm a checkout while the working tree is dirty, and
    /// returns true to proceed. Null (e.g. in tests) means "no guard, just do it".
    /// </summary>
    public Func<string, Task<bool>>? ConfirmDirtyCheckout { get; set; }

    /// <summary>Runs a checkout, but warns first when there are uncommitted (tracked) changes.</summary>
    private async Task GuardedCheckout(string target, string successMessage)
    {
        if (!await ConfirmCheckoutAllowed(target))
        {
            return;
        }

        await RunAsync(() => _git.CheckoutAsync(Repository.Path, target), successMessage);
    }

    private async Task<bool> ConfirmCheckoutAllowed(string target)
    {
        if (ConfirmDirtyCheckout is null)
        {
            return true;
        }

        // Re-read status so the guard reflects reality, not a stale snapshot. Untracked files
        // don't block a checkout, so they don't count as "dirty" for this warning.
        bool dirty;
        try
        {
            var status = await _git.GetStatusAsync(Repository.Path);
            dirty = status.Staged.Any()
                 || status.Unstaged.Any(e => e.Kind != GitChangeKind.Untracked);
        }
        catch (GitException)
        {
            dirty = !IsCleanTree;
        }

        return !dirty || await ConfirmDirtyCheckout(target);
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

        return GuardedCheckout(target, string.Format(Loc["Status_CheckedOut"], target));
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

        return GuardedCheckout(target, string.Format(Loc["Status_CheckedOut"], target));
    }

    /// <summary>
    /// Set by the View: confirms deleting a branch. Returns null to cancel, otherwise whether to
    /// force-delete an unmerged branch. Null (e.g. tests) means "no prompt, just delete safely".
    /// </summary>
    public Func<string, Task<bool?>>? ConfirmDeleteBranch { get; set; }

    /// <summary>A remote name paired with a branch on it — the answer from the Pull-from prompt.</summary>
    public sealed record RemoteBranch(string Remote, string Branch);

    /// <summary>
    /// Set by the View: given the configured remotes and the current branch, picks a remote + branch
    /// to pull. Returns null to cancel. Null delegate (e.g. tests without a UI) skips the action.
    /// </summary>
    public Func<IReadOnlyList<string>, string, Task<RemoteBranch?>>? PromptPullSource { get; set; }

    /// <summary>
    /// Set by the View: given the configured remotes and the current branch, picks the remote to push
    /// that branch to. Returns null to cancel. Null delegate (e.g. tests without a UI) skips the action.
    /// </summary>
    public Func<IReadOnlyList<string>, string, Task<string?>>? PromptPushTarget { get; set; }

    /// <summary>
    /// Deletes a local-branch badge right-clicked in the graph. The current branch is refused (git
    /// won't delete a checked-out branch); everything else confirms first (offering a force option
    /// for a branch that isn't fully merged) before the delete.
    /// </summary>
    public async Task DeleteRef(GitRef reference)
    {
        if (reference.Kind != GitRefKind.LocalBranch)
        {
            return;   // only local branches are deletable from here
        }

        var name = reference.Name;
        if (string.Equals(name, BranchName, StringComparison.Ordinal))
        {
            StatusText = string.Format(Loc["Status_CurrentBranchDelete"], name);
            return;
        }

        var force = false;
        if (ConfirmDeleteBranch is not null)
        {
            var choice = await ConfirmDeleteBranch(name);
            if (choice is null)
            {
                return;   // cancelled
            }

            force = choice.Value;
        }

        await RunAsync(() => _git.DeleteBranchAsync(Repository.Path, name, force), string.Format(Loc["Status_Deleted"], name));
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
        DiffText = Loc["Diff_Loading"];

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
                DiffText = Loc["Diff_NoTextualChanges"];
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
        DiffText = Loc["Diff_Loading"];

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
            : RunAsync(() => _git.StageAsync(Repository.Path, entry.Path), string.Format(Loc["Status_Staged"], entry.Path));
    }

    [RelayCommand]
    private Task Unstage(GitStatusEntry? entry)
    {
        entry ??= SelectedStagedFile;
        return entry is null
            ? Task.CompletedTask
            : RunAsync(() => _git.UnstageAsync(Repository.Path, entry.Path), string.Format(Loc["Status_Unstaged"], entry.Path));
    }

    /// <summary>Stages every file in one batch — the multi-select action from the Unstaged list.</summary>
    public Task StageFiles(IReadOnlyList<GitStatusEntry> files) =>
        files.Count == 0
            ? Task.CompletedTask
            : RunAsync(() => StageOrUnstageAllAsync(files, stage: true),
                files.Count == 1
                    ? string.Format(Loc["Status_Staged"], files[0].Path)
                    : string.Format(Loc["Status_StagedFiles"], files.Count));

    /// <summary>Unstages every file in one batch — the multi-select action from the Staged list.</summary>
    public Task UnstageFiles(IReadOnlyList<GitStatusEntry> files) =>
        files.Count == 0
            ? Task.CompletedTask
            : RunAsync(() => StageOrUnstageAllAsync(files, stage: false),
                files.Count == 1
                    ? string.Format(Loc["Status_Unstaged"], files[0].Path)
                    : string.Format(Loc["Status_UnstagedFiles"], files.Count));

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
    private Task StageAll() => RunAsync(() => _git.StageAllAsync(Repository.Path), Loc["Status_StagedAll"]);

    [RelayCommand]
    private Task UnstageAll() => RunAsync(() => _git.UnstageAllAsync(Repository.Path), Loc["Status_UnstagedAll"]);

    /// <summary>✨ — draft a commit message from the staged diff using the local Ollama model.</summary>
    [RelayCommand]
    private async Task GenerateCommitMessage()
    {
        if (_ai is null || _settings is null)
        {
            StatusText = Loc["Status_AiUnavailable"];
            return;
        }

        if (!HasStagedFiles)
        {
            StatusText = Loc["Status_StageFirst"];
            return;
        }

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = Loc["Status_Generating"];
        try
        {
            var diff = await _git.GetStagedDiffAsync(Repository.Path);
            var message = await _ai.GenerateAsync(diff);
            if (string.IsNullOrWhiteSpace(message))
            {
                StatusText = Loc["Status_EmptyMessage"];
            }
            else
            {
                CommitMessage = message.Trim();
                StatusText = Loc["Status_Generated"];
            }
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Commit()
    {
        if (!CanCommit)
        {
            return;
        }

        var message = CommitMessage;
        await RunAsync(() => _git.CommitAsync(Repository.Path, message), Loc["Status_Committed"]);

        // Only clear the box on success (a failed commit keeps the message for the retry).
        if (StatusText == Loc["Status_Committed"])
        {
            CommitMessage = TemplateOrEmpty;
        }
    }

    /// <summary>Stage everything, then commit it in one step (VS Code's "Commit All").</summary>
    [RelayCommand]
    private async Task CommitAll()
    {
        if (string.IsNullOrWhiteSpace(CommitMessage))
        {
            StatusText = Loc["Status_EnterMessageFirst"];
            return;
        }

        var message = CommitMessage;
        await RunAsync(
            async () =>
            {
                var staged = await _git.StageAllAsync(Repository.Path);
                return staged.Succeeded ? await _git.CommitAsync(Repository.Path, message) : staged;
            },
            Loc["Status_CommittedAll"]);

        if (StatusText == Loc["Status_CommittedAll"])
        {
            CommitMessage = TemplateOrEmpty;
        }
    }

    /// <summary>Commit the staged changes with a Signed-off-by trailer.</summary>
    [RelayCommand]
    private async Task CommitSignedOff()
    {
        if (!CanCommit)
        {
            return;
        }

        var message = CommitMessage;
        await RunAsync(() => _git.CommitAsync(Repository.Path, message, signOff: true), Loc["Status_CommittedSignedOff"]);

        if (StatusText == Loc["Status_CommittedSignedOff"])
        {
            CommitMessage = TemplateOrEmpty;
        }
    }

    /// <summary>Amend the last commit with the staged changes; an empty box keeps its message.</summary>
    [RelayCommand]
    private async Task CommitAmend()
    {
        var message = CommitMessage;
        var reworded = !string.IsNullOrWhiteSpace(message);
        await RunAsync(() => _git.CommitAmendAsync(Repository.Path, reworded ? message : null), Loc["Status_AmendedLast"]);

        if (reworded && StatusText == Loc["Status_AmendedLast"])
        {
            CommitMessage = TemplateOrEmpty;
        }
    }

    /// <summary>Stage everything, then amend the last commit with it.</summary>
    [RelayCommand]
    private async Task CommitAllAmend()
    {
        var message = CommitMessage;
        var reworded = !string.IsNullOrWhiteSpace(message);
        await RunAsync(
            async () =>
            {
                var staged = await _git.StageAllAsync(Repository.Path);
                return staged.Succeeded
                    ? await _git.CommitAmendAsync(Repository.Path, reworded ? message : null)
                    : staged;
            },
            Loc["Status_AmendedLast"]);

        if (reworded && StatusText == Loc["Status_AmendedLast"])
        {
            CommitMessage = TemplateOrEmpty;
        }
    }

    /// <summary>Undo the last commit, keeping its changes staged so nothing is lost.</summary>
    [RelayCommand]
    private Task UndoLastCommit() =>
        RunAsync(() => _git.UndoLastCommitAsync(Repository.Path), Loc["Status_UndidLastCommit"]);

    /// <summary>
    /// Set by the View: confirms discarding every change. Returns null to cancel, else whether to
    /// also delete untracked files. Null delegate (e.g. tests) means "discard tracked, keep untracked".
    /// </summary>
    public Func<Task<bool?>>? ConfirmDiscardAll { get; set; }

    /// <summary>Throw away all working-tree changes (with a confirm) — the destructive reset.</summary>
    [RelayCommand]
    private async Task DiscardAll()
    {
        if (IsCleanTree)
        {
            StatusText = Loc["Status_NothingToDiscard"];
            return;
        }

        var includeUntracked = false;
        if (ConfirmDiscardAll is not null)
        {
            var choice = await ConfirmDiscardAll();
            if (choice is null)
            {
                return;   // cancelled
            }

            includeUntracked = choice.Value;
        }

        await RunAsync(() => _git.DiscardAllAsync(Repository.Path, includeUntracked), Loc["Status_DiscardedAll"]);
    }

    /// <summary>
    /// Set by the View: confirms discarding the given paths (destructive). Returns whether to
    /// proceed. Null delegate (e.g. tests) means "proceed without prompting".
    /// </summary>
    public Func<IReadOnlyList<string>, Task<bool>>? ConfirmDiscardFiles { get; set; }

    /// <summary>Discard just the selected files' changes — the Unstaged list's right-click action.</summary>
    public async Task DiscardFiles(IReadOnlyList<GitStatusEntry> files)
    {
        if (files.Count == 0)
        {
            return;
        }

        if (ConfirmDiscardFiles is not null)
        {
            var proceed = await ConfirmDiscardFiles(files.Select(f => f.Path).ToList());
            if (!proceed)
            {
                return;
            }
        }

        var label = files.Count == 1
            ? string.Format(Loc["Status_Discarded"], files[0].Path)
            : string.Format(Loc["Status_DiscardedFiles"], files.Count);
        await RunAsync(() => DiscardEachAsync(files), label);
    }

    private async Task<GitCommandResult> DiscardEachAsync(IReadOnlyList<GitStatusEntry> files)
    {
        var result = new GitCommandResult(0, string.Empty, string.Empty);

        foreach (var file in files)
        {
            var untracked = file.Kind == GitChangeKind.Untracked;
            result = await _git.DiscardPathAsync(Repository.Path, file.Path, untracked);
            if (!result.Succeeded)
            {
                break;   // stop on the first failure; RunAsync surfaces its message
            }
        }

        return result;
    }

    [RelayCommand]
    private Task Fetch() =>
        RunAsync(() => _git.FetchAsync(Repository.Path, Progress()), Loc["Status_Fetched"]);

    [RelayCommand]
    private Task FetchPrune() =>
        RunAsync(() => _git.FetchPruneAsync(Repository.Path, Progress()), Loc["Status_FetchedPruned"]);

    [RelayCommand]
    private Task FetchAll() =>
        RunAsync(() => _git.FetchAllAsync(Repository.Path, Progress()), Loc["Status_FetchedAll"]);

    [RelayCommand]
    private Task Pull() =>
        RunAsync(() => _git.PullAsync(Repository.Path, Progress()), Loc["Status_Pulled"]);

    [RelayCommand]
    private Task PullRebase() =>
        RunAsync(() => _git.PullRebaseAsync(Repository.Path, Progress()), Loc["Status_PulledRebase"]);

    [RelayCommand]
    private async Task PullFrom()
    {
        var remotes = await _git.GetRemotesAsync(Repository.Path);
        if (remotes.Count == 0)
        {
            StatusText = Loc["Status_NoRemotes"];
            return;
        }

        if (PromptPullSource is null)
        {
            return;
        }

        var source = await PromptPullSource(remotes, BranchName);
        if (source is null || source.Branch.Length == 0)
        {
            return;   // cancelled, or no branch given
        }

        await RunAsync(
            () => _git.PullFromAsync(Repository.Path, source.Remote, source.Branch, Progress()),
            $"Pulled {source.Branch} from {source.Remote}");
    }

    [RelayCommand]
    private Task Push() =>
        RunAsync(() => _git.PushAsync(Repository.Path, Progress()), Loc["Status_Pushed"]);

    [RelayCommand]
    private async Task PushTo()
    {
        var remotes = await _git.GetRemotesAsync(Repository.Path);
        if (remotes.Count == 0)
        {
            StatusText = Loc["Status_NoRemotes"];
            return;
        }

        if (PromptPushTarget is null || BranchName.Length == 0)
        {
            return;
        }

        var remote = await PromptPushTarget(remotes, BranchName);
        if (remote is null)
        {
            return;   // cancelled
        }

        await RunAsync(
            () => _git.PushToAsync(Repository.Path, remote, BranchName, Progress()),
            $"Pushed {BranchName} to {remote}");
    }

    [RelayCommand]
    private async Task CreateBranch()
    {
        var name = NewBranchName.Trim();
        if (name.Length == 0)
        {
            return;
        }

        await RunAsync(() => _git.CreateBranchAsync(Repository.Path, name), string.Format(Loc["Status_CreatedBranch"], name));
        NewBranchName = string.Empty;
    }

    [RelayCommand]
    private Task Checkout()
    {
        if (SelectedBranch is not { IsCurrent: false } branch)
        {
            return Task.CompletedTask;
        }

        return GuardedCheckout(branch.Name, string.Format(Loc["Status_SwitchedTo"], branch.Name));
    }

    [RelayCommand]
    private Task DeleteBranch()
    {
        if (SelectedBranch is not { IsCurrent: false } branch)
        {
            return Task.CompletedTask;
        }

        return RunAsync(() => _git.DeleteBranchAsync(Repository.Path, branch.Name), string.Format(Loc["Status_Deleted"], branch.Name));
    }

    [RelayCommand]
    private Task Merge()
    {
        if (SelectedBranch is not { IsCurrent: false } branch)
        {
            return Task.CompletedTask;
        }

        return RunAsync(() => _git.MergeAsync(Repository.Path, branch.Name), string.Format(Loc["Status_Merged"], branch.Name));
    }

    [RelayCommand]
    private Task StashPush() => RunAsync(() => _git.StashPushAsync(Repository.Path), Loc["Status_Stashed"]);

    [RelayCommand]
    private Task StashPop()
    {
        if (Stashes.Count == 0)
        {
            return Task.CompletedTask;
        }

        return RunAsync(() => _git.StashPopAsync(Repository.Path), Loc["Status_PoppedStash"]);
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
        DiffText = Loc["Diff_Loading"];

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
        StatusText = Loc["Status_Working"];

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
