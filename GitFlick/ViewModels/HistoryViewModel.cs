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
/// Which column the history is ordered by. <see cref="HistoryViewModel.Graph"/> is git's own topological
/// (<c>--date-order</c>) order — the only one the lane graph can be drawn against, so any other
/// choice hides the graph. Only Author and Date are user-sortable.
/// </summary>
public enum HistorySortColumn
{
    Graph,
    Author,
    Date,
}

/// <summary>What the unified History search box searches. Message runs the client-side fuzzy
/// subject filter; File runs the git-level path filter (with all-history path autocomplete).</summary>
public enum HistorySearchType
{
    Message,
    File,
    Content,
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
/// The commit-history / lane-graph half of a repository workspace: the commit list, the lane graph,
/// filters and unified search, sort and view toggles, and per-commit detail. Extracted from
/// <see cref="WorkspaceViewModel"/> (the God object it used to live inside), which it reaches back
/// into via <c>_host</c> for the shared diff pane, status text and the git-command runner.
/// <para>
/// Members are being migrated here slice by slice; the parent still owns the working-tree/status
/// surface and the commit context-menu actions (which read <c>History.SelectedCommit</c>).
/// </para>
/// </summary>
public partial class HistoryViewModel : ViewModelBase
{
    private readonly IGitService _git;
    private readonly RepositoryItem _repository;
    private readonly ISettingsService? _settings;
    private readonly WorkspaceViewModel _host;

    /// <summary>Shorthand for the app string table (resolved in the current language).</summary>
    private static LocalizationService Loc => LocalizationService.Instance;

    public HistoryViewModel(
        IGitService git,
        RepositoryItem repository,
        ISettingsService? settings,
        WorkspaceViewModel host)
    {
        _git = git;
        _repository = repository;
        _settings = settings;
        _host = host;
    }

    /// <summary>Full-clear then refill — the same collection-swap helper the workspace uses.</summary>
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    // ── Commit list + paging ──────────────────────────────────────────────────────

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
    public partial Models.CommitGraph? Graph { get; set; }

    /// <summary>Pixels of graph on the left of each commit row, so the text clears the lanes.</summary>
    [ObservableProperty]
    public partial Thickness CommitListPadding { get; set; } = new(0);

    [ObservableProperty]
    public partial double GraphWidth { get; set; }

    // The lane graph lives in a left gutter. A repo with many concurrent branches makes it wide enough
    // to squeeze the message columns, so GraphGutter is the *displayed* gutter — capped by default so it
    // never squeezes, and draggable via the divider grip. The graph clips to it; drag wider to see more.
    private const double MinGraphGutter = 24;
    private const double DefaultGraphGutterCap = 260;
    private bool _graphGutterUserSet;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GraphGripMargin))]
    public partial double GraphGutter { get; set; }

    /// <summary>Left offset that straddles the graph-divider grip across the gutter's right edge.</summary>
    public Thickness GraphGripMargin => new(GraphGutter - 6, 0, 0, 0);

    /// <summary>Drag handler for the graph divider: sets the gutter, clamped to [min, natural width].</summary>
    public void SetGraphGutter(double width)
    {
        _graphGutterUserSet = true;
        GraphGutter = Math.Clamp(width, MinGraphGutter, Math.Max(MinGraphGutter, GraphWidth));
        RefreshGraphGutterLayout();
    }

    // Rows and the header line up against the gutter, so both insets track GraphGutter (0 when hidden).
    private void RefreshGraphGutterLayout()
    {
        CommitListPadding = ShowGraph ? new Thickness(GraphGutter + 6, 0, 0, 0) : new Thickness(0);
        HistoryHeaderMargin = new Thickness(ShowGraph ? GraphGutter + 10 : 4, 0, HistoryRightInset, 0);
    }

    [ObservableProperty]
    public partial CommitInfo? SelectedCommit { get; set; }

    // ── Commit "branches + in HEAD" info, styled as a card: the SHA, HEAD-reachability, and the branch
    //    chips containing it. Shown two ways: a popup on the hovered graph dot, and a line atop the diff.
    private readonly Dictionary<string, CommitContainment> _containmentCache = new(StringComparer.Ordinal);
    private CommitInfo? _hoverTarget;
    private CommitInfo? _selectedInfoTarget;

    /// <summary>The hovered graph dot's info (the floating popup).</summary>
    public CommitBranchInfo HoverInfo { get; } = new();

    /// <summary>The selected commit's info (the card atop the diff pane).</summary>
    public CommitBranchInfo SelectedInfo { get; } = new();

    /// <summary>Graph-dot hover: show the SHA at once, then fill branches/HEAD in place once git answers.</summary>
    public async void ShowCommitHoverInfo(CommitInfo commit)
    {
        _hoverTarget = commit;
        HoverInfo.Update(commit.ShortSha, null);
        var containment = await GetContainmentAsync(commit);
        if (ReferenceEquals(_hoverTarget, commit))   // don't overwrite once the pointer has moved on
        {
            HoverInfo.Update(commit.ShortSha, containment);
        }
    }

    /// <summary>Selected commit: the same info as a card at the top of the diff pane.</summary>
    public async void ShowSelectedCommitInfo(CommitInfo commit)
    {
        _selectedInfoTarget = commit;
        SelectedInfo.Update(commit.ShortSha, null);
        var containment = await GetContainmentAsync(commit);
        if (ReferenceEquals(_selectedInfoTarget, commit))
        {
            SelectedInfo.Update(commit.ShortSha, containment);
        }
    }

    private async Task<CommitContainment> GetContainmentAsync(CommitInfo commit)
    {
        if (_containmentCache.TryGetValue(commit.Sha, out var containment))
        {
            return containment;
        }

        try
        {
            containment = await _git.GetCommitContainmentAsync(_repository.Path, commit.Sha);
        }
        catch
        {
            containment = CommitContainment.Empty;
        }

        _containmentCache[commit.Sha] = containment;
        return containment;
    }

    /// <summary>Drops the cached commit-containment (branch tips / HEAD moved). Called by the host on refresh.</summary>
    internal void InvalidateContainment() => _containmentCache.Clear();

    /// <summary>The files the selected commit changed; picking one shows just that file's diff.</summary>
    public ObservableCollection<CommitFileEntry> CommitFiles { get; } = [];

    [ObservableProperty]
    public partial CommitFileEntry? SelectedCommitFile { get; set; }

    [ObservableProperty]
    public partial bool HasCommitFiles { get; set; }

    /// <summary>SHA whose file list is showing, so a file diff loads against the right commit.</summary>
    private string? _selectedCommitSha;

    /// <summary>Resets the selected commit's file state (host's ClearDiff delegates the commit-file half here).</summary>
    internal void ResetCommitFiles()
    {
        _selectedCommitSha = null;
        SelectedCommitFile = null;
        CommitFiles.Clear();
        HasCommitFiles = false;
    }

    /// <summary>Collapses merges to one row each: "what actually landed on this branch".</summary>
    [ObservableProperty]
    public partial bool FirstParentOnly { get; set; }

    /// <summary>Show only merge commits (<c>git log --merges</c>) — the complement of first-parent.
    /// Not a parent-closed subset, so the lane graph steps aside while it's on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGraph))]
    public partial bool MergesOnly { get; set; }

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
    public bool ShowGraph => SortColumn == HistorySortColumn.Graph
        && !HasAuthorFilter && !HasMessageFilter && !HasFileFilter && !HasContentFilter && !MergesOnly;

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

    /// <summary>
    /// Fuzzy filter on commit subjects — show only commits whose message matches. Applied
    /// client-side over the loaded history, so it's instant; like the author filter it isn't a
    /// parent-closed subset, so the lane graph steps aside while it's active.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchFilterLabel))]
    public partial string MessageFilter { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGraph))]
    [NotifyPropertyChangedFor(nameof(SearchFilterLabel))]
    [NotifyPropertyChangedFor(nameof(HasSearchFilter))]
    public partial bool HasMessageFilter { get; set; }

    partial void OnMessageFilterChanged(string value) => ApplyView();

    /// <summary>
    /// The applied file filter: show only commits that touched this pathspec. Unlike the others
    /// this is a git-level filter (git decides which commits touched the file), so changing it
    /// reloads the history. A path-filtered log isn't parent-closed either, so no graph. Set from
    /// the unified search box's File scope (see <see cref="ApplySearch"/>).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGraph))]
    [NotifyPropertyChangedFor(nameof(HasFileFilter))]
    [NotifyPropertyChangedFor(nameof(SearchFilterLabel))]
    [NotifyPropertyChangedFor(nameof(HasSearchFilter))]
    public partial string FileFilter { get; set; } = string.Empty;

    public bool HasFileFilter => FileFilter.Trim().Length > 0;

    partial void OnFileFilterChanged(string value) => HistoryLoad = LoadHistoryAsync();

    /// <summary>
    /// The applied pickaxe (content) filter: only commits that changed the number of occurrences of
    /// this string (<c>git log -S</c>). Git-level like the file filter, so it reloads and hides the graph.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGraph))]
    [NotifyPropertyChangedFor(nameof(HasContentFilter))]
    [NotifyPropertyChangedFor(nameof(SearchFilterLabel))]
    [NotifyPropertyChangedFor(nameof(HasSearchFilter))]
    public partial string ContentFilter { get; set; } = string.Empty;

    public bool HasContentFilter => ContentFilter.Trim().Length > 0;

    partial void OnContentFilterChanged(string value) => HistoryLoad = LoadHistoryAsync();

    // ── Unified History search ──────────────────────────────────────────────────
    // Message + File share one dropdown, styled like the Authors/Branches filters: a single
    // "Search ▾" button opens a flyout holding the Message/File scope toggle, the text input, and —
    // in File scope — a click-to-pick list of matching paths. Living in a flyout means it never
    // competes with the filter buttons for width, so a narrow History pane can't make it overflow.
    // The MessageFilter / FileFilter machinery above is unchanged; this just feeds it.

    /// <summary>The active search scope. Switching it clears the input and drops the other scope's filter.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFileSearch))]
    [NotifyPropertyChangedFor(nameof(IsMessageSearch))]
    [NotifyPropertyChangedFor(nameof(IsContentSearch))]
    [NotifyPropertyChangedFor(nameof(SearchPlaceholder))]
    public partial HistorySearchType SearchType { get; set; } = HistorySearchType.Message;

    public bool IsFileSearch => SearchType == HistorySearchType.File;
    public bool IsMessageSearch => SearchType == HistorySearchType.Message;
    public bool IsContentSearch => SearchType == HistorySearchType.Content;

    public string SearchPlaceholder => SearchType switch
    {
        HistorySearchType.File => Loc["History_FilePlaceholder"],
        HistorySearchType.Content => Loc["History_ContentPlaceholder"],
        _ => Loc["History_SearchMessages"],
    };

    /// <summary>The dropdown button's label — echoes the active filter, the way "Authors (2) ▾" does.</summary>
    public string SearchFilterLabel =>
        HasMessageFilter ? $"“{MessageFilter.Trim()}” ▾"
        : HasFileFilter ? $"{FileLeaf(FileFilter)} ▾"
        : HasContentFilter ? $"⌕ {ContentFilter.Trim()} ▾"
        : Loc["History_Search"] + " ▾";

    public bool HasSearchFilter => HasMessageFilter || HasFileFilter || HasContentFilter;

    /// <summary>What's typed in the search input. Message applies live; File narrows the pick list.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    // Set while a pick echoes its path into the input — suppresses the re-narrow that would otherwise
    // clear the suggestion list out from under the ListBox mid-selection (an index-out-of-range crash).
    private bool _suppressPathNarrow;

    partial void OnSearchTextChanged(string value)
    {
        // Message filters as you type (client-side, cheap). File narrows the suggestion list; the git
        // reload waits for a pick (or Enter), since each path change reloads the whole log.
        if (IsMessageSearch)
        {
            MessageFilter = value;
        }
        else if (IsFileSearch && !_suppressPathNarrow)
        {
            NarrowPathSuggestions(value);
        }
        // Content scope waits for Enter/Apply (a git reload), like File's pathspec.
    }

    /// <summary>Every path the repo has ever had (incl. deleted/renamed). Loaded once, lazily.</summary>
    public ObservableCollection<string> PathSuggestions { get; } = [];

    /// <summary>The paths matching the current input — what the File pick list shows.</summary>
    public ObservableCollection<string> FilteredPathSuggestions { get; } = [];

    private bool _pathsLoaded;

    [RelayCommand]
    private Task UseMessageSearchAsync() => SetSearchTypeAsync(HistorySearchType.Message);

    [RelayCommand]
    private Task UseFileSearchAsync() => SetSearchTypeAsync(HistorySearchType.File);

    [RelayCommand]
    private Task UseContentSearchAsync() => SetSearchTypeAsync(HistorySearchType.Content);

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
        MessageFilter = string.Empty;
        if (HasFileFilter)
        {
            FileFilter = string.Empty;
        }
        if (HasContentFilter)
        {
            ContentFilter = string.Empty;
        }
    }

    private async Task SetSearchTypeAsync(HistorySearchType type)
    {
        if (SearchType == type)
        {
            return;
        }

        SearchType = type;

        // A clean slate on every switch: drop the input and every scope's applied filter, so the list
        // is never left showing a stale filter from the scope we just left.
        SearchText = string.Empty;
        MessageFilter = string.Empty;
        if (HasFileFilter)
        {
            FileFilter = string.Empty;   // reloads the full history
        }
        if (HasContentFilter)
        {
            ContentFilter = string.Empty;
        }

        if (type == HistorySearchType.File)
        {
            await EnsurePathsLoadedAsync();
            NarrowPathSuggestions(SearchText);
        }
    }

    /// <summary>Enter in File/Content scope: apply the typed value directly (git reload).</summary>
    [RelayCommand]
    private void ApplySearch()
    {
        if (IsFileSearch)
        {
            FileFilter = SearchText.Trim();
        }
        else if (IsContentSearch)
        {
            ContentFilter = SearchText.Trim();
        }
    }

    /// <summary>A path was picked from the File list — echo it in the input and apply it as the filter.</summary>
    public void PickPath(string path)
    {
        _suppressPathNarrow = true;
        SearchText = path;          // show the pick; the narrow is suppressed so the list stays put
        _suppressPathNarrow = false;
        FileFilter = path.Trim();
    }

    // Narrow the historical-path list to the query, best fuzzy matches first, capped so a huge repo
    // doesn't render thousands of rows. An empty query shows the head of the (already sorted) list.
    private void NarrowPathSuggestions(string query)
    {
        const int Max = 60;
        FilteredPathSuggestions.Clear();

        var q = query.Trim();
        if (q.Length == 0)
        {
            var shown = 0;
            foreach (var p in PathSuggestions)
            {
                FilteredPathSuggestions.Add(p);
                if (++shown >= Max) break;
            }
            return;
        }

        var scored = new List<(string Path, int Score)>();
        foreach (var p in PathSuggestions)
        {
            if (FuzzyMatcher.TryMatch(p, q, out var score))
            {
                scored.Add((p, score));
            }
        }
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));

        for (var i = 0; i < scored.Count && i < Max; i++)
        {
            FilteredPathSuggestions.Add(scored[i].Path);
        }
    }

    // Pulls every path the repo has ever seen for the File pick list. Lazy — a session that never
    // opens File search never pays for it; a failure leaves the flag clear so a later switch retries.
    private async Task EnsurePathsLoadedAsync()
    {
        if (_pathsLoaded)
        {
            return;
        }

        _pathsLoaded = true;
        try
        {
            var paths = await _git.GetAllPathsAsync(_repository.Path);
            var sorted = new List<string>(paths);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);

            PathSuggestions.Clear();
            foreach (var path in sorted)
            {
                PathSuggestions.Add(path);
            }
        }
        catch
        {
            _pathsLoaded = false;
        }
    }

    // The tail of a pathspec, for a compact button label ("src/App.cs" -> "App.cs", "*.cs" -> "*.cs").
    private static string FileLeaf(string path)
    {
        var trimmed = path.Trim().TrimEnd('/', '\\');
        var slash = trimmed.LastIndexOfAny(['/', '\\']);
        return slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : trimmed;
    }

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

    /// <summary>The in-flight history load. Exposed so callers and tests can await it.</summary>
    public Task HistoryLoad { get; private set; } = Task.CompletedTask;

    /// <summary>Enters History: starts a fresh load and hands the caller (the host's ShowHistory) the task.</summary>
    public Task Load()
    {
        HistoryLoad = LoadHistoryAsync();
        return HistoryLoad;
    }

    /// <summary>Reloads the history and rebuilds the lane graph. Never throws.</summary>
    public async Task LoadHistoryAsync()
    {
        try
        {
            var commits = await _git.GetCommitsAsync(
                _repository.Path, _commitLimit, FirstParentOnly,
                HasFileFilter ? FileFilter.Trim() : null,
                HasContentFilter ? ContentFilter.Trim() : null,
                MergesOnly);

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
            _host.ClearDiff();
        }
        catch (GitException ex)
        {
            _host.StatusText = ex.Message;
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

    /// <summary>The complete message (subject + body) of a commit, for the "view full message" popup.</summary>
    public Task<string> GetCommitMessageAsync(CommitInfo commit) =>
        _git.GetCommitMessageAsync(_repository.Path, commit.Sha);

    /// <summary>
    /// Scope History to one file, reached from the file lists. This is just the search box's File
    /// filter driven programmatically — same mechanism, same "path ▾" chip — so there's one way to
    /// view a file's history, not two. The host flips into History mode before calling this.
    /// </summary>
    public Task ShowFileHistory(string path)
    {
        SearchType = HistorySearchType.File;      // so the search dropdown reflects the File scope
        _ = EnsurePathsLoadedAsync();             // ready the pick list in case the dropdown is opened
        SearchText = path;                        // echo the path into the search input

        if (FileFilter == path)
        {
            // Same file re-picked: the setter won't fire OnFileFilterChanged, so reload ourselves.
            HistoryLoad = LoadHistoryAsync();
        }
        else
        {
            FileFilter = path;                    // OnFileFilterChanged triggers the reload
        }

        return HistoryLoad;
    }

    partial void OnFirstParentOnlyChanged(bool value)
    {
        if (_host.IsHistoryMode)
        {
            HistoryLoad = LoadHistoryAsync();
        }
    }

    partial void OnMergesOnlyChanged(bool value)
    {
        if (_host.IsHistoryMode)
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
        var message = MessageFilter.Trim();
        HasAuthorFilter = authors.Count > 0;
        HasBranchFilter = branches.Count > 0;
        HasMessageFilter = message.Length > 0;

        // Filter in git's order first. A branch filter keeps every ancestor of the tips, so the
        // result is still a parent-closed sub-DAG the graph can be drawn against.
        IEnumerable<CommitInfo> filtered = _graphOrder;

        if (HasBranchFilter)
        {
            var reachable = CommitGraphBuilder.ReachableFrom(_graphOrder, branches);
            filtered = filtered.Where(c => reachable.Contains(c.Sha));
        }

        if (HasAuthorFilter)
        {
            filtered = filtered.Where(c => authors.Contains(c.Author));
        }

        if (HasMessageFilter)
        {
            filtered = filtered.Where(c => FuzzyMatcher.TryMatch(c.Subject, message, out _));
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

            // Default the gutter to a cap so a wide graph never squeezes the columns out of the box;
            // once the user has dragged the divider, keep their width (re-clamped to the new graph).
            GraphGutter = _graphGutterUserSet
                ? Math.Clamp(GraphGutter, MinGraphGutter, Math.Max(MinGraphGutter, GraphWidth))
                : Math.Min(GraphWidth, DefaultGraphGutterCap);
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
        RefreshGraphGutterLayout();
    }

    // OrderBy/OrderByDescending are stable, so equal keys keep their git order within the group.
    private IEnumerable<CommitInfo> Order<TKey>(IEnumerable<CommitInfo> source, Func<CommitInfo, TKey> key, IComparer<TKey> comparer) =>
        SortDescending
            ? source.OrderByDescending(key, comparer)
            : source.OrderBy(key, comparer);

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

    // ── Selected-commit detail + merge resolution ─────────────────────────────────

    partial void OnSelectedCommitChanged(CommitInfo? value)
    {
        if (_reorderingCommits)
        {
            return;
        }

        if (value is null)
        {
            SelectedInfo.Clear();
            IsMergeCommitSelected = false;
            return;
        }

        // Offer the resolution view only for a merge, and always start on the normal view — carrying
        // the toggle across selections would silently show a different diff than the one just clicked.
        _switchingCommit = true;
        IsMergeCommitSelected = value.IsMerge;
        ShowMergeResolution = false;
        _switchingCommit = false;

        _host.DiffLoad = LoadCommitFilesAsync(value);
        ShowSelectedCommitInfo(value);   // branch/HEAD card shown atop the diff pane
    }

    /// <summary>The commit's files as a folder tree — the alternative to the flat full-path list.</summary>
    public ObservableCollection<CommitFileNode> CommitFileNodes { get; } = [];

    /// <summary>
    /// Folder tree vs flat list for a commit's files. A view preference, so it persists; deep repo
    /// paths make the flat list hard to scan, while a shallow commit reads better flat.
    /// </summary>
    public bool ShowFilesAsTree
    {
        get => _settings?.Current.CommitFilesAsTree ?? false;
        set
        {
            if (_settings is null || value == ShowFilesAsTree)
            {
                return;
            }

            _settings.Current.CommitFilesAsTree = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Selection in the tree. Folders aren't files, so picking one leaves the diff alone rather than
    /// blanking it — expanding a folder shouldn't throw away what you were reading.
    /// </summary>
    [ObservableProperty]
    public partial CommitFileNode? SelectedCommitFileNode { get; set; }

    partial void OnSelectedCommitFileNodeChanged(CommitFileNode? value)
    {
        if (value?.File is { } file)
        {
            SelectedCommitFile = file;
        }
    }

    private void RebuildCommitFileNodes()
    {
        SelectedCommitFileNode = null;
        CommitFileNodes.Clear();

        foreach (var node in CommitFileNode.Build(CommitFiles))
        {
            CommitFileNodes.Add(node);
        }
    }

    /// <summary>A merge is selected, so the "what was resolved by hand" view is offered.</summary>
    [ObservableProperty]
    public partial bool IsMergeCommitSelected { get; set; }

    /// <summary>
    /// Show the merge's combined diff (what was decided while resolving) instead of what it brought in
    /// from the merged branch. Only meaningful for a merge, and reset whenever the selection moves so
    /// it can't silently stay on for the next commit.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowMergeResolution { get; set; }

    /// <summary>Set while a new commit is being selected, so resetting the toggle doesn't double-load.</summary>
    private bool _switchingCommit;

    partial void OnShowMergeResolutionChanged(bool value)
    {
        if (_switchingCommit)
        {
            return;   // OnSelectedCommitChanged loads for the new commit itself
        }

        if (SelectedCommit is { } commit)
        {
            _host.DiffLoad = LoadCommitFilesAsync(commit);
        }
    }

    /// <summary>Loads the selected commit's changed files, then shows the first file's diff.</summary>
    private async Task LoadCommitFilesAsync(CommitInfo commit)
    {
        _selectedCommitSha = commit.Sha;
        SelectedCommitFile = null;   // null-guarded, so this doesn't fire a diff load
        CommitFiles.Clear();
        HasCommitFiles = false;
        _host.DiffPath = string.Empty;
        _host.DiffText = Loc["Diff_Loading"];

        try
        {
            var resolution = ShowMergeResolution && commit.IsMerge;
            var files = resolution
                ? await _git.GetMergeResolutionFilesAsync(_repository.Path, commit.Sha)
                : await _git.GetCommitFilesAsync(_repository.Path, commit.Sha);

            // The selection may have moved on while we were awaiting — ignore a stale result.
            if (_selectedCommitSha != commit.Sha)
            {
                return;
            }

            Replace(CommitFiles, files);
            RebuildCommitFileNodes();   // the tree view mirrors the same list
            HasCommitFiles = CommitFiles.Count > 0;

            SelectedCommitFile = CommitFiles.FirstOrDefault();   // fires the file diff load
            if (SelectedCommitFile is null)
            {
                // Most merges resolve cleanly, so an empty resolution view is the normal case — say
                // that rather than the generic "no textual changes", which reads like something broke.
                _host.DiffText = resolution ? Loc["Diff_NoMergeResolution"] : Loc["Diff_NoTextualChanges"];
            }
        }
        catch (GitException ex)
        {
            _host.DiffText = ex.Message;
        }
    }

    partial void OnSelectedCommitFileChanged(CommitFileEntry? value)
    {
        if (value is null || _selectedCommitSha is null)
        {
            return;
        }

        _host.DiffLoad = ShowCommitFileDiffAsync(_selectedCommitSha, value);
    }

    private Task ShowCommitFileDiffAsync(string sha, CommitFileEntry file)
        => _host.LoadDiffAsync(
            file.Path,
            () => ShowMergeResolution && SelectedCommit?.IsMerge == true
                ? _git.GetMergeResolutionFileDiffAsync(_repository.Path, sha, file.Path)
                : _git.GetCommitFileDiffAsync(_repository.Path, sha, file.Path));
}
