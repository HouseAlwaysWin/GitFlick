using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
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
/// One row of the File-scope path autocomplete, split the way VS Code's file picker splits it: the
/// file name leads, the folder trails it in dimmer text. Whole paths alone pushed the part you're
/// actually looking for off the right edge of a narrow flyout.
/// </summary>
public sealed record PathSuggestion(string Path)
{
    private int LastSlash => Path.LastIndexOfAny(['/', '\\']);

    /// <summary>The file name — what the row leads with.</summary>
    public string Name => LastSlash >= 0 && LastSlash < Path.Length - 1 ? Path[(LastSlash + 1)..] : Path;

    /// <summary>The containing folder, empty at the repo root (nothing to show, so nothing is shown).</summary>
    public string Folder => LastSlash > 0 ? Path[..LastSlash] : string.Empty;

    public bool HasFolder => Folder.Length > 0;
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

    /// <summary>Fallback page size when there's no configured value.</summary>
    private const int DefaultPageSize = 300;

    /// <summary>
    /// Cap on how many pages one "Load more" click pages through while a client-side filter is active,
    /// so a sparse author in a huge repo can't scan the entire history in a single click.
    /// </summary>
    private const int MaxLoadMorePagesPerClick = 20;

    /// <summary>Commits fetched per page (and per "Load more"), from settings, clamped to a sane range.</summary>
    private int PageSize => System.Math.Clamp(_settings?.Current.HistoryPageSize ?? DefaultPageSize, 50, 2000);

    /// <summary>How many commits the current history load asks git for. Grows via "Load more".</summary>
    private int _commitLimit = DefaultPageSize;

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
    [NotifyPropertyChangedFor(nameof(HasAnyFilter))]
    public partial bool FirstParentOnly { get; set; }

    /// <summary>Show only merge commits (<c>git log --merges</c>) — the complement of first-parent.
    /// Not a parent-closed subset, so the lane graph steps aside while it's on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGraph))]
    [NotifyPropertyChangedFor(nameof(HasAnyFilter))]
    public partial bool MergesOnly { get; set; }

    // ── Date range (git-level --since/--until) ────────────────────────────────────
    // A git-level filter, so it pages correctly (git applies the date bound before --max-count).
    // It cuts out middle commits though, so it isn't parent-closed — the lane graph steps aside.

    /// <summary>Inclusive "from" day; null means no lower bound. Bound to the flyout's From picker.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDateFilter))]
    [NotifyPropertyChangedFor(nameof(ShowGraph))]
    [NotifyPropertyChangedFor(nameof(DateFilterLabel))]
    [NotifyPropertyChangedFor(nameof(HasAnyFilter))]
    public partial DateTime? SinceDate { get; set; }

    /// <summary>Inclusive "to" day (the whole day); null means no upper bound. Bound to the To picker.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDateFilter))]
    [NotifyPropertyChangedFor(nameof(ShowGraph))]
    [NotifyPropertyChangedFor(nameof(DateFilterLabel))]
    [NotifyPropertyChangedFor(nameof(HasAnyFilter))]
    public partial DateTime? UntilDate { get; set; }

    /// <summary>Set while a preset sets both dates at once, so we reload once instead of twice.</summary>
    private bool _suppressReload;

    /// <summary>
    /// Reload the log unless we're mid-batch. Every git-level filter (paths, pickaxe, dates,
    /// first-parent, merges-only) reloads when it changes; clearing several at once would otherwise
    /// fire one <c>git log</c> per filter, so batches raise <see cref="_suppressReload"/> and reload once.
    /// </summary>
    private void ReloadHistory()
    {
        if (!_suppressReload)
        {
            HistoryLoad = LoadHistoryAsync();
        }
    }

    /// <summary>As <see cref="ReloadHistory"/>, but only while History is actually on screen.</summary>
    private void ReloadHistoryIfActive()
    {
        if (!_suppressReload && _host.IsHistoryMode)
        {
            HistoryLoad = LoadHistoryAsync();
        }
    }

    public bool HasDateFilter => SinceDate is not null || UntilDate is not null;

    /// <summary>The "from" day at 00:00 local — what actually goes to <c>git --since</c>.</summary>
    private DateTimeOffset? SinceBound => SinceDate is { } d ? DayStart(d) : null;

    /// <summary>The "to" day at 23:59:59 local (the whole day) — what goes to <c>git --until</c>.</summary>
    private DateTimeOffset? UntilBound => UntilDate is { } d ? DayStart(d).AddDays(1).AddSeconds(-1) : null;

    private static DateTimeOffset DayStart(DateTime day) =>
        new(DateTime.SpecifyKind(day.Date, DateTimeKind.Unspecified), DateTimeOffset.Now.Offset);

    /// <summary>Compact label for the "Dates ▾" button; shows the active range once one is set.</summary>
    public string DateFilterLabel => (SinceDate, UntilDate) switch
    {
        (null, null) => Loc["History_DateFilter"] + " ▾",
        ({ } s, { } u) => $"{s:M/d}–{u:M/d} ▾",
        ({ } s, null) => $"≥{s:M/d} ▾",
        (null, { } u) => $"≤{u:M/d} ▾",
    };

    partial void OnSinceDateChanged(DateTime? value) => ReloadForDateChange();
    partial void OnUntilDateChanged(DateTime? value) => ReloadForDateChange();

    private void ReloadForDateChange() => ReloadHistoryIfActive();

    // Presets set both ends at once; SetDateRange suppresses the per-property reload and reloads once.
    [RelayCommand]
    private void Today()
    {
        var t = DateTime.Today;
        SetDateRange(t, t);
    }

    [RelayCommand]
    private void Last7Days() => SetDateRange(DateTime.Today.AddDays(-6), DateTime.Today);

    [RelayCommand]
    private void Last30Days() => SetDateRange(DateTime.Today.AddDays(-29), DateTime.Today);

    [RelayCommand]
    private void ThisMonth()
    {
        var now = DateTime.Today;
        SetDateRange(new DateTime(now.Year, now.Month, 1), now);
    }

    [RelayCommand]
    private void ClearDateFilter() => SetDateRange(null, null);

    private void SetDateRange(DateTime? from, DateTime? to)
    {
        _suppressReload = true;
        SinceDate = from;
        UntilDate = to;
        _suppressReload = false;

        ReloadHistoryIfActive();
    }

    [ObservableProperty]
    public partial bool HasCommits { get; set; }

    /// <summary>Which column the list is ordered by. Anything but <see cref="HistorySortColumn.Graph"/> hides the graph.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGraph))]
    [NotifyPropertyChangedFor(nameof(AuthorSortGlyph))]
    [NotifyPropertyChangedFor(nameof(DateSortGlyph))]
    [NotifyPropertyChangedFor(nameof(AuthorColumnHeader))]
    [NotifyPropertyChangedFor(nameof(DateColumnHeader))]
    [NotifyPropertyChangedFor(nameof(HasAnyFilter))]
    public partial HistorySortColumn SortColumn { get; set; } = HistorySortColumn.Graph;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AuthorSortGlyph))]
    [NotifyPropertyChangedFor(nameof(DateSortGlyph))]
    [NotifyPropertyChangedFor(nameof(AuthorColumnHeader))]
    [NotifyPropertyChangedFor(nameof(DateColumnHeader))]
    public partial bool SortDescending { get; set; }

    /// <summary>
    /// Whether the lane graph shows. It needs git's order (no column sort) and a parent-closed set.
    /// A branch filter keeps a valid sub-DAG (all ancestors of the tips), so the graph is rebuilt
    /// for it; an author filter does not (an author's commits link through other people's), so it
    /// stays hidden there.
    /// </summary>
    public bool ShowGraph => SortColumn == HistorySortColumn.Graph
        && !HasAuthorFilter && !HasMessageFilter && !HasFileFilter && !HasContentFilter && !MergesOnly
        && !HasDateFilter && !HasFileExclude;

    // The active column wears an arrow; the rest show nothing.
    public string AuthorSortGlyph => GlyphFor(HistorySortColumn.Author);
    public string DateSortGlyph => GlyphFor(HistorySortColumn.Date);

    // Label + arrow together, because the two sortable headers are Buttons: a StringFormat can't join
    // a translated string to a bound glyph, which is how they ended up hardcoded in English.
    public string AuthorColumnHeader => Loc["History_Col_Author"] + AuthorSortGlyph;
    public string DateColumnHeader => Loc["History_Col_Date"] + DateSortGlyph;

    private string GlyphFor(HistorySortColumn column) =>
        SortColumn == column ? (SortDescending ? " ▼" : " ▲") : string.Empty;

    /// <summary>Distinct authors in the loaded history; ticking any narrows the list to those.</summary>
    public ObservableCollection<FilterOption> AuthorFilters { get; } = [];

    /// <summary>The authors matching <see cref="AuthorFilterSearch"/> — what the flyout actually shows.</summary>
    public ObservableCollection<FilterOption> FilteredAuthorFilters { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGraph))]
    [NotifyPropertyChangedFor(nameof(AuthorFilterLabel))]
    [NotifyPropertyChangedFor(nameof(HasAnyFilter))]
    public partial bool HasAuthorFilter { get; set; }

    public string AuthorFilterLabel => HasAuthorFilter
        ? $"{Loc["History_AuthorsLabel"]} ({AuthorFilters.Count(a => a.IsSelected)}) ▾"
        : Loc["History_AuthorsLabel"] + " ▾";

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
    [NotifyPropertyChangedFor(nameof(HasAnyFilter))]
    public partial bool HasBranchFilter { get; set; }

    public string BranchFilterLabel => HasBranchFilter
        ? $"{Loc["History_BranchesLabel"]} ({BranchFilters.Count(b => b.IsSelected)}) ▾"
        : Loc["History_BranchesLabel"] + " ▾";

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
    [NotifyPropertyChangedFor(nameof(HasAnyFilter))]
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
    [NotifyPropertyChangedFor(nameof(HasAnyFilter))]
    public partial string FileFilter { get; set; } = string.Empty;

    public bool HasFileFilter => FileFilter.Trim().Length > 0;

    partial void OnFileFilterChanged(string value) => ReloadHistory();

    /// <summary>
    /// The applied pickaxe (content) filter: only commits that changed the number of occurrences of
    /// this string (<c>git log -S</c>). Git-level like the file filter, so it reloads and hides the graph.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGraph))]
    [NotifyPropertyChangedFor(nameof(HasContentFilter))]
    [NotifyPropertyChangedFor(nameof(SearchFilterLabel))]
    [NotifyPropertyChangedFor(nameof(HasSearchFilter))]
    [NotifyPropertyChangedFor(nameof(HasAnyFilter))]
    public partial string ContentFilter { get; set; } = string.Empty;

    public bool HasContentFilter => ContentFilter.Trim().Length > 0;

    partial void OnContentFilterChanged(string value) => ReloadHistory();

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
    [NotifyPropertyChangedFor(nameof(ShowIncludeBox))]
    [NotifyPropertyChangedFor(nameof(ShowExcludeBox))]
    [NotifyPropertyChangedFor(nameof(CanUseRegex))]
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

    /// <summary>The dropdown button's label — echoes every active filter, the way "Authors (2) ▾" does.
    /// The excluded paths wear a ≠, so a narrowed result set is never a mystery.</summary>
    public string SearchFilterLabel
    {
        get
        {
            var parts = new List<string>();
            if (HasMessageFilter)
            {
                parts.Add($"“{MessageFilter.Trim()}”");
            }

            if (HasContentFilter)
            {
                parts.Add($"⌕ {ContentFilter.Trim()}");
            }

            if (HasFileFilter)
            {
                parts.Add(FileLeaf(FileFilter));
            }

            if (HasFileExclude)
            {
                parts.Add($"≠{FileLeaf(FileExcludeFilter)}");
            }

            return parts.Count == 0 ? Loc["History_Search"] + " ▾" : string.Join(" ", parts) + " ▾";
        }
    }

    public bool HasSearchFilter =>
        HasMessageFilter || HasFileFilter || HasContentFilter || HasFileExclude;

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

    // ── Search modifiers and path scoping (VS Code-style) ───────────────────────
    // Laid out like VS Code's Search panel: the query box (with Aa / .* inside it) searches, and two
    // separate boxes scope it to file paths — "include paths" and "exclude paths", both plain git
    // pathspecs. The query and the path scope are orthogonal, so "message contains fix, only under
    // src/, ignoring *.md" is one search. The scope radio only decides what the QUERY box searches;
    // in File scope the query IS the path, so its include box would be a duplicate and hides.

    /// <summary>Treat the query as a regular expression (Message: .NET; Content: git --pickaxe-regex).</summary>
    [ObservableProperty]
    public partial bool SearchUseRegex { get; set; }

    /// <summary>Match the query's letter case exactly (Message: fuzzy/regex; File: pathspec; Content: git -i).</summary>
    [ObservableProperty]
    public partial bool SearchCaseSensitive { get; set; }

    /// <summary>Regex mode is on but the query doesn't parse — shown inline; the filter doesn't apply.</summary>
    [ObservableProperty]
    public partial bool SearchRegexInvalid { get; set; }

    // Flipping a modifier re-runs whichever filter kind is active: content (pickaxe) and File
    // (pathspec) live in git, so they reload; the message filter is client-side and just re-applies.
    partial void OnSearchUseRegexChanged(bool value) => ReapplySearchModifiers();
    partial void OnSearchCaseSensitiveChanged(bool value) => ReapplySearchModifiers();

    private void ReapplySearchModifiers()
    {
        if (_suppressReload)
        {
            return;   // a batch (Clear filters) is reloading once at the end
        }

        if (HasContentFilter || (IsFileSearch && HasFileFilter))
        {
            HistoryLoad = LoadHistoryAsync();
        }
        else
        {
            ApplyView();
        }
    }

    /// <summary>What's typed in the "include paths" box (Message/Content scope). Applies on Enter.</summary>
    [ObservableProperty]
    public partial string IncludeText { get; set; } = string.Empty;

    /// <summary>What's typed in the "exclude paths" box. Applies to history on Enter.</summary>
    [ObservableProperty]
    public partial string ExcludeText { get; set; } = string.Empty;

    // The pick list is client-side, so it can honour the exclusion as you type — without that, typing
    // an exclude and still being offered the very paths it drops reads as "the exclude did nothing".
    partial void OnExcludeTextChanged(string value)
    {
        if (IsFileSearch && !_suppressPathNarrow)
        {
            NarrowPathSuggestions(SearchText);
        }
    }

    /// <summary>Drop commits that only touched these paths — git pathspec ":(exclude)". Reloads.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGraph))]
    [NotifyPropertyChangedFor(nameof(HasFileExclude))]
    [NotifyPropertyChangedFor(nameof(SearchFilterLabel))]
    [NotifyPropertyChangedFor(nameof(HasSearchFilter))]
    [NotifyPropertyChangedFor(nameof(HasAnyFilter))]
    public partial string FileExcludeFilter { get; set; } = string.Empty;

    public bool HasFileExclude => FileExcludeFilter.Trim().Length > 0;

    partial void OnFileExcludeFilterChanged(string value) => ReloadHistory();

    /// <summary>Enter in the include-paths box: commit it as the pathspec (a git reload).</summary>
    [RelayCommand]
    private void ApplyInclude() => FileFilter = IncludeText.Trim();

    /// <summary>Enter in the exclude-paths box: commit it as the ":(exclude)" pathspec (a git reload).</summary>
    [RelayCommand]
    private void ApplyExclude() => FileExcludeFilter = ExcludeText.Trim();

    // Path scoping only belongs to the scopes with a file dimension. Message searches commit text, so
    // it's just the query box; File's query already IS the include path, leaving it only an exclude;
    // Content searches inside files, so both path boxes narrow it usefully.

    /// <summary>Only Content needs a separate include box — File's query is the include, Message has no paths.</summary>
    public bool ShowIncludeBox => IsContentSearch;

    /// <summary>Excluding paths applies wherever files are involved — File and Content.</summary>
    public bool ShowExcludeBox => !IsMessageSearch;

    /// <summary>git pathspec has no regex form, so .* can't apply to a File-scope query.</summary>
    public bool CanUseRegex => !IsFileSearch;

    /// <summary>Case folding for the File-scope query only — the include/exclude boxes stay git-default.</summary>
    private bool PathQueryIgnoreCase => IsFileSearch && !SearchCaseSensitive;

    /// <summary>
    /// Builds a subject predicate for the current modifiers, or null when the regex doesn't parse —
    /// the caller then skips that filter, which beats blanking the whole history (or, for the
    /// exclude side, hiding everything) while a pattern is half-typed.
    /// </summary>
    private Func<string, bool>? BuildMessagePredicate(string query)
    {
        if (SearchUseRegex)
        {
            try
            {
                var regex = new Regex(query, SearchCaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                return s => regex.IsMatch(s);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        var caseSensitive = SearchCaseSensitive;
        return s => FuzzyMatcher.TryMatch(s, query, caseSensitive, out _);
    }

    /// <summary>Every path the repo has ever had (incl. deleted/renamed). Loaded once, lazily.</summary>
    public ObservableCollection<string> PathSuggestions { get; } = [];

    /// <summary>The paths matching the current input — what the File pick list shows.</summary>
    public ObservableCollection<PathSuggestion> FilteredPathSuggestions { get; } = [];

    /// <summary>Drives the pick list's visibility: it drops down only once the query matches something.</summary>
    [ObservableProperty]
    public partial bool HasPathSuggestions { get; set; }

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
        IncludeText = string.Empty;
        ExcludeText = string.Empty;
        if (HasFileFilter)
        {
            FileFilter = string.Empty;
        }
        if (HasContentFilter)
        {
            ContentFilter = string.Empty;
        }
        if (HasFileExclude)
        {
            FileExcludeFilter = string.Empty;
        }
    }

    private async Task SetSearchTypeAsync(HistorySearchType type)
    {
        if (SearchType == type)
        {
            return;
        }

        SearchType = type;

        // A clean slate on every switch: drop the inputs and every scope's applied filter, so the list
        // is never left showing a stale filter from the scope we just left.
        SearchText = string.Empty;
        MessageFilter = string.Empty;
        IncludeText = string.Empty;
        ExcludeText = string.Empty;
        if (HasFileExclude)
        {
            FileExcludeFilter = string.Empty;   // reloads the full history
        }
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
        else
        {
            HasPathSuggestions = false;   // the pick list belongs to File scope only
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
    // doesn't render thousands of rows. An empty query shows nothing — the list is an autocomplete,
    // so it drops down only once you've typed, like every other suggestion list here.
    // The exclude box applies too, live: it's the same set of paths the filter will drop, so
    // still offering them would contradict what the user just typed.
    private void NarrowPathSuggestions(string query)
    {
        const int Max = 60;
        FilteredPathSuggestions.Clear();

        var excluded = ExcludeText.Trim();
        bool Kept(string path) => excluded.Length == 0 || !PathGlob.MatchesAny(path, excluded);

        var q = query.Trim();
        if (q.Length == 0)
        {
            HasPathSuggestions = false;
            return;
        }

        var scored = new List<(string Path, int Score)>();
        foreach (var p in PathSuggestions)
        {
            if (Kept(p) && FuzzyMatcher.TryMatch(p, q, out var score))
            {
                scored.Add((p, score));
            }
        }
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));

        for (var i = 0; i < scored.Count && i < Max; i++)
        {
            FilteredPathSuggestions.Add(new PathSuggestion(scored[i].Path));
        }

        HasPathSuggestions = FilteredPathSuggestions.Count > 0;
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

    /// <summary>Keep the Message column at least this readable — matches its MinWidth in the XAML.</summary>
    internal const double MinMessageColumnWidth = 80;

    /// <summary>Floor for each fixed column — matches the grips' MinColumnWidth in the view.</summary>
    internal const double MinFixedColumnWidth = 50;

    /// <summary>
    /// Clamp the three fixed columns so they and a minimum Message column fit in
    /// <paramref name="availableWidth"/>. Widths that already fit come back untouched; overflow is
    /// shrunk proportionally, flooring each column — so a narrowed pane squeezes the table instead
    /// of pushing Date and Commit off the edge.
    /// </summary>
    internal static (double Author, double Date, double Commit) ClampColumns(
        double availableWidth, double author, double date, double commit)
    {
        var budget = Math.Max(availableWidth - MinMessageColumnWidth, 3 * MinFixedColumnWidth);
        var total = author + date + commit;
        if (total <= budget)
        {
            return (author, date, commit);
        }

        // Proportional shrink with a floor. The floor can leave a few px of overflow at extreme
        // widths, which clips gracefully rather than fighting the minimums.
        var scale = budget / total;
        return (Math.Max(MinFixedColumnWidth, author * scale),
                Math.Max(MinFixedColumnWidth, date * scale),
                Math.Max(MinFixedColumnWidth, commit * scale));
    }

    /// <summary>Re-fit the fixed columns to the pane — called by the view whenever the pane resizes.</summary>
    public void ClampColumnsToPane(double paneWidth)
    {
        var margin = HistoryHeaderMargin;   // absorbs the graph gutter (left) and scrollbar (right)
        var (author, date, commit) = ClampColumns(
            paneWidth - margin.Left - margin.Right,
            AuthorColumnWidth.Value, DateColumnWidth.Value, CommitColumnWidth.Value);

        if (Math.Abs(author - AuthorColumnWidth.Value) > 0.5)
        {
            AuthorColumnWidth = new GridLength(author);
        }

        if (Math.Abs(date - DateColumnWidth.Value) > 0.5)
        {
            DateColumnWidth = new GridLength(date);
        }

        if (Math.Abs(commit - CommitColumnWidth.Value) > 0.5)
        {
            CommitColumnWidth = new GridLength(commit);
        }
    }

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
        // Start each visit at one page, honouring the (possibly changed) configured size, rather than
        // inheriting however far a previous visit had grown the window.
        _commitLimit = PageSize;
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
                MergesOnly,
                SinceBound, UntilBound,
                HasFileExclude ? FileExcludeFilter.Trim() : null,
                contentRegex: HasContentFilter && SearchUseRegex,
                contentIgnoreCase: HasContentFilter && !SearchCaseSensitive,
                pathIncludeIgnoreCase: PathQueryIgnoreCase);

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

    /// <summary>
    /// Grows the history window and reloads, keeping the selected commit. The client-side filters
    /// (author/branch/message) whittle the loaded window, so one more raw page can add no *visible*
    /// rows; keep paging until the visible list actually grows, history ends, or a per-click cap is
    /// hit — so one click always makes visible progress instead of "nothing happened". With no
    /// client-side filter the visible list grows on the first page, so the loop runs exactly once.
    /// </summary>
    [RelayCommand]
    private async Task LoadMoreCommits()
    {
        var keepSha = SelectedCommit?.Sha;
        var before = Commits.Count;
        var pages = 0;

        do
        {
            _commitLimit += PageSize;
            await LoadHistoryAsync();
            pages++;
        }
        while (Commits.Count == before && HasMoreCommits && pages < MaxLoadMorePagesPerClick);

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

    partial void OnFirstParentOnlyChanged(bool value) => ReloadHistoryIfActive();

    partial void OnMergesOnlyChanged(bool value) => ReloadHistoryIfActive();

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

    /// <summary>
    /// Anything at all is narrowing the view. Drives the "Clear filters" button, which only appears
    /// when it would do something — the toolbar is busy enough without a permanently dead button.
    /// </summary>
    public bool HasAnyFilter =>
        HasSearchFilter || HasAuthorFilter || HasBranchFilter || HasDateFilter
        || FirstParentOnly || MergesOnly || SortColumn != HistorySortColumn.Graph;

    /// <summary>
    /// Drops every filter at once. Several of these are git-level, so each would reload the log on its
    /// own — the batch suppresses that and issues a single reload at the end.
    /// </summary>
    [RelayCommand]
    private void ClearAllFilters()
    {
        _suppressReload = true;
        _suppressFilterApply = true;

        foreach (var item in AuthorFilters)
        {
            item.IsSelected = false;
        }

        foreach (var item in BranchFilters)
        {
            item.IsSelected = false;
        }

        AuthorFilterSearch = string.Empty;
        BranchFilterSearch = string.Empty;

        // Search: the query, its scope modifiers, and both path boxes.
        SearchType = HistorySearchType.Message;
        SearchText = string.Empty;
        MessageFilter = string.Empty;
        IncludeText = string.Empty;
        ExcludeText = string.Empty;
        FileFilter = string.Empty;
        ContentFilter = string.Empty;
        FileExcludeFilter = string.Empty;
        SearchUseRegex = false;
        SearchCaseSensitive = false;
        SearchRegexInvalid = false;
        FilteredPathSuggestions.Clear();
        HasPathSuggestions = false;

        SinceDate = null;
        UntilDate = null;
        FirstParentOnly = false;
        MergesOnly = false;

        SortColumn = HistorySortColumn.Graph;
        SortDescending = false;

        _suppressFilterApply = false;
        _suppressReload = false;

        // One reload for the lot; LoadHistoryAsync re-runs ApplyView at the end.
        HistoryLoad = LoadHistoryAsync();

        // The "(N)" counts come from collections that don't raise on their own.
        OnPropertyChanged(nameof(AuthorFilterLabel));
        OnPropertyChanged(nameof(BranchFilterLabel));
        OnPropertyChanged(nameof(HasAnyFilter));
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

        // Include and exclude are two independent filters that combine (VS Code-style). A regex that
        // doesn't parse skips just that side and raises the inline flag.
        SearchRegexInvalid = false;
        if (HasMessageFilter)
        {
            if (BuildMessagePredicate(message) is { } matches)
            {
                filtered = filtered.Where(c => matches(c.Subject));
            }
            else
            {
                SearchRegexInvalid = true;
            }
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
