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

/// <summary>One tag in the Tags list — tickable for multi-delete, and marked by whether it's on the remote.</summary>
public partial class TagItem : ObservableObject
{
    public TagItem(string name) => Name = name;

    public string Name { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>null = unknown (offline / not checked), true = on the remote, false = local-only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnRemote))]
    [NotifyPropertyChangedFor(nameof(IsLocalOnly))]
    public partial bool? OnRemote { get; set; }

    public bool IsOnRemote => OnRemote == true;
    public bool IsLocalOnly => OnRemote == false;
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

    public ObservableCollection<TagItem> Tags { get; } = [];

    /// <summary>Tag names known to exist on the remote; null until <see cref="RefreshRemoteTagsAsync"/> runs.</summary>
    private HashSet<string>? _remoteTagNames;

    /// <summary>Snapshot of the git command log, filled when the log flyout opens.</summary>
    public ObservableCollection<GitCommandLogEntry> CommandLog { get; } = [];

    public bool HasCommandLog => CommandLog.Count > 0;

    /// <summary>Pull the newest most-recent-first snapshot of the git command log into the view.</summary>
    public void RefreshCommandLog()
    {
        Replace(CommandLog, _git.CommandLog.Snapshot());
        OnPropertyChanged(nameof(HasCommandLog));
    }

    /// <summary>Recent HEAD moves (git reflog), filled when the reflog window opens.</summary>
    public ObservableCollection<ReflogEntry> Reflog { get; } = [];

    public bool HasReflog => Reflog.Count > 0;

    /// <summary>Load the reflog into the view (best-effort; never throws).</summary>
    public async Task LoadReflogAsync()
    {
        try
        {
            Replace(Reflog, await _git.GetReflogAsync(Repository.Path));
        }
        catch (GitException ex)
        {
            StatusText = ex.Message;
        }

        OnPropertyChanged(nameof(HasReflog));
    }

    /// <summary>Reset HEAD to a reflog entry (soft/mixed/hard) — the recovery action.</summary>
    [RelayCommand]
    private async Task ResetToReflog(ReflogEntry? entry)
    {
        if (entry is null || PromptResetMode is null)
        {
            return;
        }

        var mode = await PromptResetMode(entry.ShortSha);
        if (mode is null)
        {
            return;
        }

        await RunAsync(
            () => _git.ResetToAsync(Repository.Path, entry.Sha, mode.Value),
            string.Format(Loc["Status_Reset"], entry.ShortSha));
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

    /// <summary>HEAD isn't on a branch, so there's no branch to publish or track.</summary>
    [ObservableProperty]
    public partial bool IsDetachedHead { get; set; }

    // ── Accounts ────────────────────────────────────────────────────────────────────────────────
    // Two independent things, which is the whole point of showing them together: the identity commits
    // are authored as, and the GitHub account allowed to push. Them disagreeing is the classic trap.

    private readonly GitHubAccountService _github = new();

    /// <summary>Effective commit author for this repo (repo-local value beats global).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IdentityLabel))]
    [NotifyPropertyChangedFor(nameof(IdentitySummary))]
    public partial GitIdentity Identity { get; set; } = GitIdentity.None;

    /// <summary>Short label for the toolbar button.</summary>
    public string IdentityLabel => Identity.IsConfigured ? Identity.Name : Loc["Account_NoIdentity"];

    /// <summary>"Martin Wang &lt;…&gt;" plus where it came from, for the flyout.</summary>
    public string IdentitySummary => Identity.IsConfigured
        ? $"{Identity} · {Loc[Identity.IsRepoOverride ? "Account_ScopeRepo" : "Account_ScopeGlobal"]}"
        : Loc["Account_NoIdentity_Hint"];

    /// <summary>Edit fields in the flyout, seeded from the effective identity.</summary>
    [ObservableProperty]
    public partial string IdentityNameInput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IdentityEmailInput { get; set; } = string.Empty;

    /// <summary>Apply to this repo only rather than globally.</summary>
    [ObservableProperty]
    public partial bool IdentityRepoOnly { get; set; }

    /// <summary>Identities saved for one-click switching.</summary>
    public ObservableCollection<SavedIdentity> SavedIdentities { get; } = [];

    /// <summary>GitHub accounts gh is signed in to.</summary>
    public ObservableCollection<GhAccount> GitHubAccounts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGitHubAccount))]
    public partial string ActiveGitHubLogin { get; set; } = string.Empty;

    public bool HasGitHubAccount => ActiveGitHubLogin.Length > 0;

    /// <summary>gh is installed; when false the flyout shows an install hint instead of buttons.</summary>
    [ObservableProperty]
    public partial bool IsGhAvailable { get; set; } = true;

    /// <summary>
    /// Set by the View: confirms signing a GitHub account out of gh (login). Null (tests) = no prompt.
    /// </summary>
    public Func<string, Task<bool>>? ConfirmGitHubLogout { get; set; }

    /// <summary>Reloads the effective identity and seeds the edit fields.</summary>
    private async Task RefreshIdentityAsync()
    {
        try
        {
            Identity = await _git.GetIdentityAsync(Repository.Path);
        }
        catch (GitException)
        {
            // A failed read is not "no identity configured". Blanking it here would claim the user has
            // none — the one message this feature must never get wrong — so keep the last known value.
            return;
        }

        IdentityNameInput = Identity.Name;
        IdentityEmailInput = Identity.Email;
        IdentityRepoOnly = Identity.IsRepoOverride;

        Replace(SavedIdentities, _settings?.Current.SavedIdentities ?? []);
    }

    /// <summary>Reloads the gh account list. Best-effort: gh is optional.</summary>
    [RelayCommand]
    private async Task RefreshAccounts()
    {
        var accounts = await _github.GetAccountsAsync();
        IsGhAvailable = _github.IsAvailable;

        Replace(GitHubAccounts, accounts);
        ActiveGitHubLogin = accounts.FirstOrDefault(a => a.IsActive)?.Login ?? string.Empty;
    }

    /// <summary>Writes the edited identity to the chosen scope.</summary>
    [RelayCommand]
    private async Task ApplyIdentity()
    {
        var name = IdentityNameInput.Trim();
        var email = IdentityEmailInput.Trim();
        if (name.Length == 0 || email.Length == 0)
        {
            return;
        }

        await RunAsync(
            () => _git.SetIdentityAsync(Repository.Path, name, email, global: !IdentityRepoOnly),
            string.Format(Loc["Status_IdentitySet"], name));

        RememberIdentity(name, email);
        await RefreshIdentityAsync();
    }

    /// <summary>Drops the repo-local identity so this repo follows the global one again.</summary>
    [RelayCommand]
    private async Task ClearRepoIdentity()
    {
        await RunAsync(() => _git.ClearRepoIdentityAsync(Repository.Path), Loc["Status_IdentityCleared"]);
        await RefreshIdentityAsync();
    }

    /// <summary>One-click switch to a saved identity, at the currently selected scope.</summary>
    [RelayCommand]
    private async Task UseSavedIdentity(SavedIdentity? saved)
    {
        if (saved is null)
        {
            return;
        }

        IdentityNameInput = saved.Name;
        IdentityEmailInput = saved.Email;
        await ApplyIdentity();
    }

    [RelayCommand]
    private void GitHubLogin()
    {
        if (!_github.StartLogin())
        {
            IsGhAvailable = false;
            return;
        }

        StatusText = Loc["Status_GitHubLoginStarted"];
    }

    [RelayCommand]
    private async Task SwitchGitHubAccount(GhAccount? account)
    {
        if (account is null || account.IsActive)
        {
            return;
        }

        StatusText = await _github.SwitchAsync(account.Host, account.Login)
            ? string.Format(Loc["Status_GitHubSwitched"], account.Login)
            : Loc["Status_GitHubSwitchFailed"];

        await RefreshAccountsCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task GitHubLogout(GhAccount? account)
    {
        if (account is null)
        {
            return;
        }

        if (ConfirmGitHubLogout is not null && !await ConfirmGitHubLogout(account.Login))
        {
            return;
        }

        StatusText = await _github.LogoutAsync(account.Host, account.Login)
            ? string.Format(Loc["Status_GitHubLoggedOut"], account.Login)
            : Loc["Status_GitHubLogoutFailed"];

        await RefreshAccountsCommand.ExecuteAsync(null);
    }

    // Keeps the switcher list useful without asking the user to curate it by hand.
    private void RememberIdentity(string name, string email)
    {
        if (_settings is null)
        {
            return;
        }

        var known = _settings.Current.SavedIdentities;
        if (known.Any(i => string.Equals(i.Email, email, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(i.Name, name, StringComparison.Ordinal)))
        {
            return;
        }

        known.Add(new SavedIdentity { Name = name, Email = email });
        _settings.Save();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAhead))]
    [NotifyPropertyChangedFor(nameof(PushLabel))]
    [NotifyPropertyChangedFor(nameof(PushTooltip))]
    public partial int Ahead { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBehind))]
    [NotifyPropertyChangedFor(nameof(PullLabel))]
    [NotifyPropertyChangedFor(nameof(PullTooltip))]
    public partial int Behind { get; set; }

    /// <summary>Local branch is ahead of its upstream — there's something to push.</summary>
    public bool IsAhead => Ahead > 0;

    /// <summary>Upstream has commits we don't — the Pull button lights up to say "pull to sync".</summary>
    public bool IsBehind => Behind > 0;

    /// <summary>Pull button label — carries the behind count as a nudge when the upstream is ahead.</summary>
    public string PullLabel => IsBehind ? $"{Loc["Toolbar_Pull"]} ↓{Behind}" : Loc["Toolbar_Pull"];

    /// <summary>Pull button tooltip — spells out the "pull to sync" reminder while behind.</summary>
    public string PullTooltip => IsBehind
        ? string.Format(Loc["Sync_PullReminder_Tooltip"], Behind)
        : Loc["Toolbar_Pull_Tooltip"];

    /// <summary>Push button label — carries the ahead count as a nudge when there's something to push.</summary>
    public string PushLabel => IsAhead ? $"{Loc["Toolbar_Push"]} ↑{Ahead}" : Loc["Toolbar_Push"];

    /// <summary>Push button tooltip — spells out the "push to sync" reminder while ahead.</summary>
    public string PushTooltip => IsAhead
        ? string.Format(Loc["Sync_PushReminder_Tooltip"], Ahead)
        : Loc["Toolbar_Push_Tooltip"];

    /// <summary>A quiet background fetch is running to refresh the ahead/behind counts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorking))]
    public partial bool IsCheckingRemote { get; set; }

    /// <summary>
    /// Anything is in flight — a command, or the quiet background fetch. Drives the loading line at the
    /// top of the window, which is the only signal a long operation gets: the background fetch
    /// deliberately doesn't touch the status text, and a toolbar label would reflow the toolbar.
    /// </summary>
    public bool IsWorking => IsBusy || IsCheckingRemote;

    [ObservableProperty]
    public partial GitStatusEntry? SelectedUnstagedFile { get; set; }

    [ObservableProperty]
    public partial GitStatusEntry? SelectedStagedFile { get; set; }

    [ObservableProperty]
    public partial GitBranch? SelectedBranch { get; set; }

    [ObservableProperty]
    public partial string NewBranchName { get; set; } = string.Empty;

    /// <summary>
    /// Ref the new branch starts from. Defaults to the checked-out branch (git's own default), but can
    /// be any branch, tag or SHA — typing one that isn't in <see cref="BaseRefs"/> is allowed.
    /// </summary>
    [ObservableProperty]
    public partial string NewBranchFrom { get; set; } = string.Empty;

    /// <summary>Completions for "create from": local branches first, then remote-tracking ones.</summary>
    public ObservableCollection<string> BaseRefs { get; } = [];

    /// <summary>Fuzzy query that narrows the Branch-flyout list, so a repo with many branches stays findable.</summary>
    [ObservableProperty]
    public partial string BranchSearch { get; set; } = string.Empty;

    partial void OnBranchSearchChanged(string value) => NarrowBranches();

    /// <summary>Branches matching <see cref="BranchSearch"/> — what the Branch flyout list actually shows.</summary>
    public ObservableCollection<GitBranch> FilteredBranches { get; } = [];

    // Copies the fuzzy-matching branches (all when the query is empty) into FilteredBranches, keeping the
    // current selection if it still matches so the action buttons stay pointed at the same branch.
    private void NarrowBranches()
    {
        var query = BranchSearch.Trim();
        var keep = SelectedBranch;
        FilteredBranches.Clear();

        foreach (var branch in Branches)
        {
            if (query.Length == 0 || FuzzyMatcher.TryMatch(branch.Name, query, out _))
            {
                FilteredBranches.Add(branch);
            }
        }

        if (keep is not null && FilteredBranches.Contains(keep))
        {
            SelectedBranch = keep;
        }
    }

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

    [ObservableProperty]
    public partial bool HasTags { get; set; }

    /// <summary>How many tags are ticked — drives the "Delete (N)" bulk actions.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedTags))]
    [NotifyPropertyChangedFor(nameof(AllTagsSelected))]
    public partial int SelectedTagCount { get; set; }

    public bool HasSelectedTags => SelectedTagCount > 0;

    private bool _suppressTagRecalc;

    /// <summary>The header "select all" tick: checked only when every tag is ticked; setting it ticks
    /// or clears them all in one shot.</summary>
    public bool AllTagsSelected
    {
        get => Tags.Count > 0 && SelectedTagCount == Tags.Count;
        set
        {
            _suppressTagRecalc = true;
            foreach (var tag in Tags)
            {
                tag.IsSelected = value;
            }
            _suppressTagRecalc = false;

            SelectedTagCount = Tags.Count(t => t.IsSelected);
            OnPropertyChanged();
        }
    }

    /// <summary>True for a repo with no pending changes, so the UI can say so plainly.</summary>
    [ObservableProperty]
    public partial bool IsCleanTree { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    [NotifyPropertyChangedFor(nameof(IsWorking))]
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
            containment = await _git.GetCommitContainmentAsync(Repository.Path, commit.Sha);
        }
        catch
        {
            containment = CommitContainment.Empty;
        }

        _containmentCache[commit.Sha] = containment;
        return containment;
    }

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
            var paths = await _git.GetAllPathsAsync(Repository.Path);
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
                Repository.Path, _commitLimit, FirstParentOnly,
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

    /// <summary>The complete message (subject + body) of a commit, for the "view full message" popup.</summary>
    public Task<string> GetCommitMessageAsync(CommitInfo commit) =>
        _git.GetCommitMessageAsync(Repository.Path, commit.Sha);

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

    /// <summary>
    /// Scope History to one file, reached from the file lists. This is just the search box's File
    /// filter driven programmatically — same mechanism, same "path ▾" chip — so there's one way to
    /// view a file's history, not two.
    /// </summary>
    public Task ShowFileHistory(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return Task.CompletedTask;
        }

        IsHistoryMode = true;                     // jump to History if invoked from the Changes tab
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

    /// <summary>Create a compare view for two refs (<c>base..compare</c>); the View opens it in a window.</summary>
    public CompareViewModel CreateCompare(string baseRef, string compareRef) =>
        new(_git, Repository.Path, baseRef, compareRef);

    /// <summary>Create a blame view for a file (optionally as of <paramref name="rev"/>); the View opens it in a window.</summary>
    public BlameViewModel CreateBlame(string path, string? rev = null) =>
        new(_git, Repository.Path, path, rev);

    partial void OnFirstParentOnlyChanged(bool value)
    {
        if (IsHistoryMode)
        {
            HistoryLoad = LoadHistoryAsync();
        }
    }

    partial void OnMergesOnlyChanged(bool value)
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
        var message = MessageFilter.Trim();
        HasAuthorFilter = authors.Count > 0;
        HasBranchFilter = branches.Count > 0;
        HasMessageFilter = message.Length > 0;

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
    /// What the "Pull from…" prompt needs: the configured remotes, the branch being pulled into, and
    /// a lookup for the branches on a given remote so the branch box can offer completions. The
    /// lookup is a delegate rather than a fixed list because the answer changes with the remote the
    /// user picks in the dialog.
    /// </summary>
    public sealed record PullSourceOptions(
        IReadOnlyList<string> Remotes,
        string CurrentBranch,
        Func<string, IReadOnlyList<string>> BranchesOn);

    /// <summary>
    /// Set by the View: picks a remote + branch to pull. Returns null to cancel. Null delegate
    /// (e.g. tests without a UI) skips the action.
    /// </summary>
    public Func<PullSourceOptions, Task<RemoteBranch?>>? PromptPullSource { get; set; }

    /// <summary>
    /// Set by the View: given the configured remotes and the current branch, picks the remote to push
    /// that branch to. Returns null to cancel. Null delegate (e.g. tests without a UI) skips the action.
    /// </summary>
    public Func<IReadOnlyList<string>, string, Task<string?>>? PromptPushTarget { get; set; }

    /// <summary>
    /// Set by the View: confirms publishing a branch the remote doesn't have yet (branch, remote).
    /// Null (e.g. tests) means "no prompt, just publish".
    /// </summary>
    public Func<string, string, Task<bool>>? ConfirmPublishBranch { get; set; }

    /// <summary>Set by the View: opens a URL in the default browser (side-effect kept injectable for tests).</summary>
    public Action<string>? OpenUrlInBrowser { get; set; }

    /// <summary>Set by the View: copies text to the clipboard.</summary>
    public Func<string, Task>? SetClipboardText { get; set; }

    /// <summary>Set by the View: pick one item from a list (used by set-upstream and compare). Args are the
    /// candidates and a localized prompt line. Returns the pick, or null to cancel.</summary>
    public Func<IReadOnlyList<string>, string, Task<string?>>? PromptPickRef { get; set; }

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
            string.Format(Loc["Status_CherryPicked"], commit.ShortSha));
    }

    // Set by the View. Each returns the entered value (or null to cancel); a null delegate (e.g. in
    // tests, no UI) skips the action.
    public Func<Task<string?>>? PromptTagName { get; set; }
    public Func<Task<string?>>? PromptBranchName { get; set; }
    public Func<string, Task<GitResetMode?>>? PromptResetMode { get; set; }
    public Func<string, Task<bool>>? ConfirmRebase { get; set; }

    /// <summary>Tags the selected commit (lightweight) after prompting for a name.</summary>
    [RelayCommand]
    private async Task AddTag()
    {
        if (SelectedCommit is not { } commit || PromptTagName is null)
        {
            return;
        }

        var name = await PromptTagName();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await RunAsync(() => _git.CreateTagAsync(Repository.Path, name, commit.Sha), string.Format(Loc["Status_Tagged"], name));
    }

    // ── Tags (toolbar) ────────────────────────────────────────────────────────────
    /// <summary>Create a tag at HEAD (the current commit) after prompting for a name.</summary>
    [RelayCommand]
    private async Task CreateTag()
    {
        if (PromptTagName is null)
        {
            return;
        }

        var name = await PromptTagName();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await RunAsync(
            () => _git.CreateTagAsync(Repository.Path, name.Trim(), "HEAD"),
            string.Format(Loc["Status_Tagged"], name.Trim()));
        await RefreshRemoteTagsAsync();   // the new tag shows as local-only until pushed
    }

    /// <summary>Delete the ticked tags locally.</summary>
    [RelayCommand]
    private async Task DeleteSelectedTags()
    {
        var names = Tags.Where(t => t.IsSelected).Select(t => t.Name).ToList();
        if (names.Count == 0)
        {
            return;
        }

        await RunAsync(
            () => _git.DeleteTagsAsync(Repository.Path, names),
            string.Format(Loc["Status_TagsDeleted"], names.Count));
    }

    /// <summary>Delete the ticked tags on the remote.</summary>
    [RelayCommand]
    private async Task DeleteSelectedRemoteTags()
    {
        var names = Tags.Where(t => t.IsSelected).Select(t => t.Name).ToList();
        if (names.Count == 0)
        {
            return;
        }

        var remote = await FirstRemoteOrReportAsync();
        if (remote is null)
        {
            return;
        }

        await RunAsync(
            () => _git.DeleteRemoteTagsAsync(Repository.Path, remote, names),
            string.Format(Loc["Status_RemoteTagsDeleted"], names.Count, remote));
        await RefreshRemoteTagsAsync();
    }

    [RelayCommand]
    private async Task PushTags()
    {
        if (Tags.Count == 0)
        {
            return;
        }

        var remote = await FirstRemoteOrReportAsync();
        if (remote is null)
        {
            return;
        }

        await RunAsync(
            () => _git.PushTagsAsync(Repository.Path, remote, Progress()),
            string.Format(Loc["Status_TagsPushed"], remote));
        await RefreshRemoteTagsAsync();
    }

    // Tag remote ops target the first remote (origin in the common single-remote case). Reports and bails if none.
    private async Task<string?> FirstRemoteOrReportAsync()
    {
        var remotes = await _git.GetRemotesAsync(Repository.Path);
        if (remotes.Count == 0)
        {
            StatusText = Loc["Status_NoRemotes"];
            return null;
        }

        return remotes[0];
    }

    // ── Open on remote (GitHub/GitLab) ────────────────────────────────────────────
    // The first remote's URL, or null (with a status message) when there's no remote / no URL.
    private async Task<string?> FirstRemoteUrlAsync()
    {
        var remote = await FirstRemoteOrReportAsync();
        if (remote is null)
        {
            return null;
        }

        var url = await _git.GetRemoteUrlAsync(Repository.Path, remote);
        if (string.IsNullOrEmpty(url))
        {
            StatusText = Loc["Status_NoRemoteUrl"];
        }

        return url;
    }

    // Build a web link with RemoteUrlBuilder, then open or copy it; reports if the URL can't be parsed.
    private async Task LaunchOrCopyAsync(Func<string, string?> build, bool copy)
    {
        var remoteUrl = await FirstRemoteUrlAsync();
        if (remoteUrl is null)
        {
            return;
        }

        var webUrl = build(remoteUrl);
        if (webUrl is null)
        {
            StatusText = Loc["Status_RemoteUrlUnparsed"];
            return;
        }

        if (copy)
        {
            if (SetClipboardText is not null)
            {
                await SetClipboardText(webUrl);
            }
            StatusText = Loc["Status_CopiedLink"];
        }
        else
        {
            OpenUrlInBrowser?.Invoke(webUrl);
            StatusText = Loc["Status_OpenedRemote"];
        }
    }

    [RelayCommand]
    private Task OpenCommitOnRemote() => SelectedCommit is { } c
        ? LaunchOrCopyAsync(url => RemoteUrlBuilder.Commit(url, c.Sha), copy: false)
        : Task.CompletedTask;

    [RelayCommand]
    private Task CopyCommitLink() => SelectedCommit is { } c
        ? LaunchOrCopyAsync(url => RemoteUrlBuilder.Commit(url, c.Sha), copy: true)
        : Task.CompletedTask;

    /// <summary>Open a ref (branch) badge on the remote.</summary>
    [RelayCommand]
    private Task OpenRefOnRemote(GitRef? reference) => reference is null
        ? Task.CompletedTask
        : LaunchOrCopyAsync(url => RemoteUrlBuilder.Branch(url, LeafRefName(reference)), copy: false);

    /// <summary>Open a working-tree file on the remote at the current branch.</summary>
    [RelayCommand]
    private Task OpenFileOnRemote(string? path) => string.IsNullOrEmpty(path) || BranchName.Length == 0
        ? Task.CompletedTask
        : LaunchOrCopyAsync(url => RemoteUrlBuilder.File(url, BranchName, path), copy: false);

    // Remote badges arrive as "origin/main"; the web /tree/ link wants just the branch part.
    private static string LeafRefName(GitRef reference)
    {
        if (reference.Kind == GitRefKind.RemoteBranch)
        {
            var slash = reference.Name.IndexOf('/');
            return slash >= 0 ? reference.Name[(slash + 1)..] : reference.Name;
        }

        return reference.Name;
    }

    /// <summary>Creates a branch at the selected commit (without switching) after prompting for a name.</summary>
    [RelayCommand]
    private async Task CreateBranchHere()
    {
        if (SelectedCommit is not { } commit || PromptBranchName is null)
        {
            return;
        }

        var name = await PromptBranchName();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await RunAsync(() => _git.CreateBranchAtAsync(Repository.Path, name, commit.Sha), string.Format(Loc["Status_CreatedBranchAt"], name));
    }

    /// <summary>Reverts the selected commit (records a new commit that undoes it).</summary>
    [RelayCommand]
    private Task RevertCommit()
    {
        if (SelectedCommit is not { } commit)
        {
            return Task.CompletedTask;
        }

        return RunAsync(() => _git.RevertAsync(Repository.Path, commit.Sha), string.Format(Loc["Status_Reverted"], commit.ShortSha));
    }

    /// <summary>Merges the selected commit into the current branch.</summary>
    [RelayCommand]
    private Task MergeCommit()
    {
        if (SelectedCommit is not { } commit)
        {
            return Task.CompletedTask;
        }

        return RunAsync(() => _git.MergeAsync(Repository.Path, commit.Sha), string.Format(Loc["Status_Merged"], commit.ShortSha));
    }

    /// <summary>Rebases the current branch onto the selected commit (confirmed — it rewrites history).</summary>
    [RelayCommand]
    private async Task RebaseOnto()
    {
        if (SelectedCommit is not { } commit)
        {
            return;
        }

        if (ConfirmRebase is not null && !await ConfirmRebase(commit.ShortSha))
        {
            return;
        }

        await RunAsync(() => _git.RebaseOntoAsync(Repository.Path, commit.Sha), string.Format(Loc["Status_Rebased"], commit.ShortSha));
    }

    /// <summary>Moves the current branch to the selected commit; the prompt picks soft/mixed/hard.</summary>
    [RelayCommand]
    private async Task ResetToCommit()
    {
        if (SelectedCommit is not { } commit || PromptResetMode is null)
        {
            return;
        }

        var mode = await PromptResetMode(commit.ShortSha);
        if (mode is null)
        {
            return;
        }

        await RunAsync(() => _git.ResetToAsync(Repository.Path, commit.Sha, mode.Value), string.Format(Loc["Status_Reset"], commit.ShortSha));
    }

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

        DiffLoad = LoadCommitFilesAsync(value);
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
            DiffLoad = LoadCommitFilesAsync(commit);
        }
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
            var resolution = ShowMergeResolution && commit.IsMerge;
            var files = resolution
                ? await _git.GetMergeResolutionFilesAsync(Repository.Path, commit.Sha)
                : await _git.GetCommitFilesAsync(Repository.Path, commit.Sha);

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
                DiffText = resolution ? Loc["Diff_NoMergeResolution"] : Loc["Diff_NoTextualChanges"];
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
            var diff = ShowMergeResolution && SelectedCommit?.IsMerge == true
                ? await _git.GetMergeResolutionFileDiffAsync(Repository.Path, sha, file.Path)
                : await _git.GetCommitFileDiffAsync(Repository.Path, sha, file.Path);

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
            IsDetachedHead = status.IsDetached;
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
            NarrowBranches();   // keep the Branch-flyout's filtered view in sync with the refreshed list
            _containmentCache.Clear();   // branch tips / HEAD moved, so cached commit-containment is stale
            await RefreshBaseRefsAsync();
            await RefreshIdentityAsync();

            var stashes = await _git.GetStashesAsync(Repository.Path);
            Replace(Stashes, stashes);
            HasStashes = Stashes.Count > 0;

            var tags = await _git.GetTagsAsync(Repository.Path);
            RebuildTags(tags);
        }
        catch (GitException ex)
        {
            StatusText = ex.Message;
        }
    }

    // Rebuild the tag list as tickable TagItems, marking each with its last-known remote presence.
    private void RebuildTags(IReadOnlyList<GitTag> tags)
    {
        foreach (var existing in Tags)
        {
            existing.PropertyChanged -= OnTagSelectionChanged;
        }

        Tags.Clear();
        foreach (var tag in tags)
        {
            var item = new TagItem(tag.Name)
            {
                OnRemote = _remoteTagNames?.Contains(tag.Name),
            };
            item.PropertyChanged += OnTagSelectionChanged;
            Tags.Add(item);
        }

        HasTags = Tags.Count > 0;
        SelectedTagCount = 0;
    }

    private void OnTagSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_suppressTagRecalc && e.PropertyName == nameof(TagItem.IsSelected))
        {
            SelectedTagCount = Tags.Count(t => t.IsSelected);
        }
    }

    // Best-effort: learn which tags are on the remote (git ls-remote) and mark the list. Leaves the
    // markers unknown when offline / no remote, so nothing is wrongly labelled local-only.
    private async Task RefreshRemoteTagsAsync()
    {
        try
        {
            var remotes = await _git.GetRemotesAsync(Repository.Path);
            if (remotes.Count == 0)
            {
                _remoteTagNames = null;
                return;
            }

            var names = await _git.GetRemoteTagNamesAsync(Repository.Path, remotes[0]);
            _remoteTagNames = new HashSet<string>(names, StringComparer.Ordinal);

            foreach (var tag in Tags)
            {
                tag.OnRemote = _remoteTagNames.Contains(tag.Name);
            }
        }
        catch (GitException)
        {
            // Offline / auth — leave the markers as they were (unknown).
        }
    }

    /// <summary>
    /// Opened flow: show the working tree immediately, then quietly fetch so the ahead/behind counts
    /// (and the "pull first" reminder) reflect the real remote, not just what we last knew locally.
    /// </summary>
    public async Task OpenedAsync()
    {
        await RefreshAsync();
        await CheckRemoteAsync();
    }

    /// <summary>
    /// A best-effort background fetch that refreshes just the sync counts. It never blocks the toolbar
    /// (no <see cref="IsBusy"/>) and swallows failures — offline, no upstream, or uncached credentials
    /// simply leave the last-known counts in place (GIT_TERMINAL_PROMPT=0 stops it hanging on a prompt).
    /// </summary>
    public async Task CheckRemoteAsync()
    {
        if (IsCheckingRemote)
        {
            return;
        }

        try
        {
            var remotes = await _git.GetRemotesAsync(Repository.Path);
            if (remotes.Count == 0)
            {
                return;   // nothing to sync against
            }

            IsCheckingRemote = true;
            await _git.FetchAsync(Repository.Path);

            var status = await _git.GetStatusAsync(Repository.Path);
            Ahead = status.Ahead;
            Behind = status.Behind;
            Upstream = status.Upstream;
        }
        catch (GitException)
        {
            // Offline / auth / no upstream — keep whatever we already knew.
        }
        finally
        {
            IsCheckingRemote = false;
        }

        // Mark which tags are already on the remote (best-effort; its own error handling).
        await RefreshRemoteTagsAsync();
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

    // Completions for "create branch from": local branches first (the common case), then the
    // remote-tracking ones, since branching off origin/… is just as routine. Best-effort — a repo with
    // no remote simply gets the local list, and the box takes free text either way.
    private async Task RefreshBaseRefsAsync()
    {
        var refs = new List<string>(Branches.Select(b => b.Name));

        try
        {
            refs.AddRange(await _git.GetRemoteBranchesAsync(Repository.Path));
        }
        catch (GitException)
        {
            // No remote, or git refused — the local branches are still worth offering.
        }

        Replace(BaseRefs, refs);

        // Default to branching off where you are, which is what git does with no start point.
        if (NewBranchFrom.Length == 0)
        {
            NewBranchFrom = BranchName;
        }
    }

    /// <summary>
    /// Narrows "origin/main"-style remote-tracking refs to one remote and strips the prefix, because
    /// <c>git pull &lt;remote&gt; &lt;branch&gt;</c> wants the bare branch name.
    /// </summary>
    internal static IReadOnlyList<string> BranchesOnRemote(IReadOnlyList<string> remoteBranches, string remote)
    {
        var prefix = remote + "/";
        var names = new List<string>();

        foreach (var full in remoteBranches)
        {
            if (full.StartsWith(prefix, StringComparison.Ordinal) && full.Length > prefix.Length)
            {
                names.Add(full[prefix.Length..]);
            }
        }

        return names;
    }

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

        // Every remote-tracking branch, once ("origin/main", "upstream/dev"). The dialog narrows them
        // to whichever remote is picked, so switching remotes doesn't cost another git call.
        var remoteBranches = await _git.GetRemoteBranchesAsync(Repository.Path);

        var options = new PullSourceOptions(
            remotes,
            BranchName,
            remote => BranchesOnRemote(remoteBranches, remote));

        var source = await PromptPullSource(options);
        if (source is null || source.Branch.Length == 0)
        {
            return;   // cancelled, or no branch given
        }

        await RunAsync(
            () => _git.PullFromAsync(Repository.Path, source.Remote, source.Branch, Progress()),
            $"Pulled {source.Branch} from {source.Remote}");
    }

    /// <summary>
    /// Push. A branch the remote has never seen has no upstream, and plain <c>git push</c> just fails
    /// with a wall of advice — so offer to publish it (push --set-upstream) instead, the way other
    /// clients do. Anything else falls through to the ordinary push.
    /// </summary>
    [RelayCommand]
    private async Task Push()
    {
        if (!string.IsNullOrEmpty(Upstream) || BranchName.Length == 0 || IsDetachedHead)
        {
            await RunAsync(() => _git.PushAsync(Repository.Path, Progress()), Loc["Status_Pushed"]);
            return;
        }

        var remotes = await _git.GetRemotesAsync(Repository.Path);
        if (remotes.Count == 0)
        {
            StatusText = Loc["Status_NoRemotes"];
            return;
        }

        // One remote is the normal case; with several, ask which — same picker as "Push to…".
        var remote = remotes.Count == 1 ? remotes[0] : await (PromptPushTarget?.Invoke(remotes, BranchName) ?? Task.FromResult<string?>(null));
        if (remote is null)
        {
            return;   // cancelled
        }

        if (ConfirmPublishBranch is not null && !await ConfirmPublishBranch(BranchName, remote))
        {
            return;
        }

        await RunAsync(
            () => _git.PublishBranchAsync(Repository.Path, remote, BranchName, Progress()),
            string.Format(Loc["Status_PublishedBranch"], BranchName, remote));
    }

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

        // Blank, or already the checked-out branch, means "from HEAD" — leave it to git.
        var from = NewBranchFrom.Trim();
        var startPoint = from.Length == 0 || from == BranchName ? null : from;

        await RunAsync(
            () => _git.CreateBranchAsync(Repository.Path, name, checkout: true, startPoint),
            startPoint is null
                ? string.Format(Loc["Status_CreatedBranch"], name)
                : string.Format(Loc["Status_CreatedBranchFrom"], name, startPoint));

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

    /// <summary>Rename the selected branch (current branch allowed) after prompting for a new name.</summary>
    [RelayCommand]
    private async Task RenameBranch()
    {
        if (SelectedBranch is not { } branch || PromptBranchName is null)
        {
            return;
        }

        var newName = await PromptBranchName();
        if (string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        await RunAsync(
            () => _git.RenameBranchAsync(Repository.Path, branch.Name, newName.Trim()),
            string.Format(Loc["Status_RenamedBranch"], newName.Trim()));
    }

    /// <summary>Set the selected branch's upstream, picked from the remote-tracking branches.</summary>
    [RelayCommand]
    private async Task SetUpstream()
    {
        if (SelectedBranch is not { } branch || PromptPickRef is null)
        {
            return;
        }

        var remoteBranches = await _git.GetRemoteBranchesAsync(Repository.Path);
        if (remoteBranches.Count == 0)
        {
            StatusText = Loc["Status_NoRemoteBranches"];
            return;
        }

        var upstream = await PromptPickRef(remoteBranches, string.Format(Loc["Branch_SetUpstream_Prompt"], branch.Name));
        if (string.IsNullOrEmpty(upstream))
        {
            return;
        }

        await RunAsync(
            () => _git.SetUpstreamAsync(Repository.Path, branch.Name, upstream),
            string.Format(Loc["Status_SetUpstream"], upstream));
    }

    [RelayCommand]
    private Task UnsetUpstream() => SelectedBranch is { } branch
        ? RunAsync(() => _git.UnsetUpstreamAsync(Repository.Path, branch.Name), Loc["Status_UnsetUpstream"])
        : Task.CompletedTask;

    // ── Stash: create ───────────────────────────────────────────────────────────
    [RelayCommand]
    private Task StashPush() => RunAsync(() => _git.StashPushAsync(Repository.Path), Loc["Status_Stashed"]);

    [RelayCommand]
    private Task StashUntracked() =>
        RunAsync(() => _git.StashPushAsync(Repository.Path, includeUntracked: true), Loc["Status_Stashed"]);

    [RelayCommand]
    private Task StashStaged() =>
        RunAsync(() => _git.StashPushAsync(Repository.Path, stagedOnly: true), Loc["Status_Stashed"]);

    // ── Stash: act on the latest ──────────────────────────────────────────────────
    [RelayCommand]
    private Task ApplyLatestStash() => Stashes.Count == 0
        ? Task.CompletedTask
        : RunAsync(() => _git.StashApplyAsync(Repository.Path), Loc["Status_StashApplied"]);

    [RelayCommand]
    private Task StashPop() => Stashes.Count == 0
        ? Task.CompletedTask
        : RunAsync(() => _git.StashPopAsync(Repository.Path), Loc["Status_PoppedStash"]);

    [RelayCommand]
    private Task DropAllStashes() => Stashes.Count == 0
        ? Task.CompletedTask
        : RunAsync(() => _git.StashClearAsync(Repository.Path), Loc["Status_StashCleared"]);

    // ── Stash: act on a specific entry (from the list's per-item actions) ─────────
    [RelayCommand]
    private Task ApplyStash(StashEntry? entry) => entry is null
        ? Task.CompletedTask
        : RunAsync(() => _git.StashApplyAsync(Repository.Path, entry.Index), Loc["Status_StashApplied"]);

    [RelayCommand]
    private Task PopStashAt(StashEntry? entry) => entry is null
        ? Task.CompletedTask
        : RunAsync(() => _git.StashPopAsync(Repository.Path, entry.Index), Loc["Status_PoppedStash"]);

    [RelayCommand]
    private Task DropStash(StashEntry? entry) => entry is null
        ? Task.CompletedTask
        : RunAsync(() => _git.StashDropAsync(Repository.Path, entry.Index), Loc["Status_StashDropped"]);

    [RelayCommand]
    private async Task ViewStash(StashEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        // Drop any file/commit selection so the diff pane shows the stash's patch.
        SelectedUnstagedFile = null;
        SelectedStagedFile = null;
        ClearDiff();
        DiffPath = entry.Description;
        DiffText = Loc["Diff_Loading"];

        try
        {
            var diff = await _git.GetStashDiffAsync(Repository.Path, entry.Index);
            DiffText = diff.Trim().Length == 0 ? Loc["Diff_NoTextualChanges"] : diff;
        }
        catch (GitException ex)
        {
            DiffText = ex.Message;
        }
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
