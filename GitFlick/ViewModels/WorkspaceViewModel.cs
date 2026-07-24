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
using GitFlick.Services.Updates;

namespace GitFlick.ViewModels;

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
        History = new HistoryViewModel(git, repository, settings, this);
        CommitMessage = TemplateOrEmpty;   // start a fresh commit from the template
    }

    public RepositoryItem Repository { get; }

    /// <summary>The commit-history / lane-graph subsystem for this workspace (extracted God-object slice).</summary>
    public HistoryViewModel History { get; }

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
            ? Loc["Model_Downloaded"]
            : string.Format(Loc["Model_NotDownloaded"], SelectedBuiltinModel.SizeDisplay);

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
            var progress = new Progress<ArtifactDownloadProgress>(p => ModelDownloadProgress = p.Percentage);
            await new ArtifactDownloader(SharedHttpClient.Instance).DownloadAsync(
                CommitModelCatalog.DescriptorFor(preset), CommitModelCatalog.ModelsDirectory, progress);
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

    /// <summary>Remote-tracking branches ("origin/main", …) shown under the flyout's REMOTE section.</summary>
    public ObservableCollection<GitBranch> RemoteBranches { get; } = [];

    public ObservableCollection<StashEntry> Stashes { get; } = [];

    public ObservableCollection<TagItem> Tags { get; } = [];

    /// <summary>Tag names known to exist on the remote; null until <see cref="RefreshRemoteTagsAsync"/> runs.</summary>
    private HashSet<string>? _remoteTagNames;

    /// <summary>Snapshot of the git command log, filled when the log flyout opens.</summary>
    public ObservableCollection<GitCommandLogEntry> CommandLog { get; } = [];

    public bool HasCommandLog => CommandLog.Count > 0;

    /// <summary>The command whose output the log window shows below the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedCommandOutput))]
    public partial GitCommandLogEntry? SelectedCommandLogEntry { get; set; }

    public bool HasSelectedCommandOutput => SelectedCommandLogEntry is { HasOutput: true };

    /// <summary>Pull the newest most-recent-first snapshot of the git command log into the view.</summary>
    public void RefreshCommandLog()
    {
        Replace(CommandLog, _git.CommandLog.Snapshot());
        SelectedCommandLogEntry = null;
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
    [NotifyPropertyChangedFor(nameof(BranchTitle))]
    public partial string BranchName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BranchTitle))]
    public partial string? Upstream { get; set; }

    /// <summary>HEAD isn't on a branch, so there's no branch to publish or track.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BranchTitle))]
    public partial bool IsDetachedHead { get; set; }

    /// <summary>
    /// The branch as the title bar shows it, with where it lives: a tracked branch names its upstream,
    /// one that's never been pushed says so. (HEAD is always on a LOCAL branch — checking out
    /// "origin/main" detaches — so the useful distinction isn't local-vs-remote but tracked-vs-not.)
    /// </summary>
    public string BranchTitle
    {
        get
        {
            if (BranchName.Length == 0)
            {
                return string.Empty;
            }

            if (IsDetachedHead)
            {
                return BranchName;   // already reads "(detached)"
            }

            return string.IsNullOrEmpty(Upstream)
                ? $"{BranchName} ({Loc["Title_LocalOnly"]})"
                : $"{BranchName} → {Upstream}";
        }
    }

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

    // Two lists (local + remote) but one logical selection. Binding both ListBoxes to a single
    // SelectedItem doesn't work: the second list doesn't contain the first list's item, so it coerces
    // the shared binding back to null. Instead each list gets its own selection property, picking in
    // one clears the other, and SelectedBranch is the derived "whichever is active" the buttons read.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedBranch))]
    [NotifyPropertyChangedFor(nameof(SelectedIsLocal))]
    [NotifyPropertyChangedFor(nameof(CanPublishSelected))]
    public partial GitBranch? SelectedLocalBranch { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedBranch))]
    [NotifyPropertyChangedFor(nameof(SelectedIsLocal))]
    [NotifyPropertyChangedFor(nameof(CanPublishSelected))]
    public partial GitBranch? SelectedRemoteBranch { get; set; }

    // Picking in one list clears the other, so the two stay mutually exclusive. Only act on a non-null
    // pick — clearing the other to null re-enters here with null, which is the no-op that stops a loop.
    partial void OnSelectedLocalBranchChanged(GitBranch? value)
    {
        if (value is not null)
        {
            SelectedRemoteBranch = null;
        }
    }

    partial void OnSelectedRemoteBranchChanged(GitBranch? value)
    {
        if (value is not null)
        {
            SelectedLocalBranch = null;
        }
    }

    /// <summary>
    /// The branch the flyout's action buttons operate on: whichever list has a pick (they're mutually
    /// exclusive). Settable so refresh and tests can restore a local selection the old way.
    /// </summary>
    public GitBranch? SelectedBranch
    {
        get => SelectedLocalBranch ?? SelectedRemoteBranch;
        set => SelectedLocalBranch = value;
    }

    /// <summary>The selection is a local branch — rename/upstream/delete/publish only apply to those.</summary>
    public bool SelectedIsLocal => SelectedBranch is { IsRemote: false };

    /// <summary>A local branch the remote hasn't seen yet — the Publish button pushes --set-upstream it.</summary>
    public bool CanPublishSelected => SelectedBranch is { IsRemote: false, Upstream: null };

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

    /// <summary>Local branches matching <see cref="BranchSearch"/> — the flyout's LOCAL list.</summary>
    public ObservableCollection<GitBranch> FilteredBranches { get; } = [];

    /// <summary>Remote-tracking branches matching <see cref="BranchSearch"/> — the flyout's REMOTE list.</summary>
    public ObservableCollection<GitBranch> FilteredRemoteBranches { get; } = [];

    /// <summary>Whether the REMOTE section has anything to show, so it hides for a remote-less repo.</summary>
    [ObservableProperty]
    public partial bool HasRemoteBranches { get; set; }

    // Copies the fuzzy-matching branches (all when the query is empty) into the two filtered lists,
    // keeping the current selection if it still matches so the action buttons stay pointed at the same
    // branch. Clearing a bound list momentarily nulls its ListBox selection; the re-pin below restores it.
    private void NarrowBranches()
    {
        var query = BranchSearch.Trim();
        var keepLocal = SelectedLocalBranch;
        var keepRemote = SelectedRemoteBranch;

        FilteredBranches.Clear();
        foreach (var branch in Branches)
        {
            if (query.Length == 0 || FuzzyMatcher.TryMatch(branch.Name, query, out _))
            {
                FilteredBranches.Add(branch);
            }
        }

        FilteredRemoteBranches.Clear();
        foreach (var branch in RemoteBranches)
        {
            if (query.Length == 0 || FuzzyMatcher.TryMatch(branch.Name, query, out _))
            {
                FilteredRemoteBranches.Add(branch);
            }
        }

        if (keepLocal is not null && FilteredBranches.Contains(keepLocal))
        {
            SelectedLocalBranch = keepLocal;
        }

        if (keepRemote is not null && FilteredRemoteBranches.Contains(keepRemote))
        {
            SelectedRemoteBranch = keepRemote;
        }

        HasRemoteBranches = FilteredRemoteBranches.Count > 0;
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

    /// <summary>The in-flight diff load. Exposed so callers and tests can await it. History assigns it too.</summary>
    public Task DiffLoad { get; internal set; } = Task.CompletedTask;

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
        return History.Load();
    }

    /// <summary>
    /// Scope History to one file, reached from the file lists. This is just the search box's File
    /// filter driven programmatically — same mechanism, same "path ▾" chip — so there's one way to
    /// view a file's history, not two. The History VM owns the search/filter machinery now.
    /// </summary>
    public Task ShowFileHistory(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return Task.CompletedTask;
        }

        IsHistoryMode = true;                 // jump to History if invoked from the Changes tab
        return History.ShowFileHistory(path);
    }

    /// <summary>Create a compare view for two refs (<c>base..compare</c>); the View opens it in a window.</summary>
    public CompareViewModel CreateCompare(string baseRef, string compareRef) =>
        new(_git, Repository.Path, baseRef, compareRef);

    /// <summary>Create a blame view for a file (optionally as of <paramref name="rev"/>); the View opens it in a window.</summary>
    public BlameViewModel CreateBlame(string path, string? rev = null) =>
        new(_git, Repository.Path, path, rev);

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
        if (History.SelectedCommit is not { } commit)
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
        if (History.SelectedCommit is not { } commit)
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
        if (History.SelectedCommit is not { } commit || PromptTagName is null)
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
    private Task OpenCommitOnRemote() => History.SelectedCommit is { } c
        ? LaunchOrCopyAsync(url => RemoteUrlBuilder.Commit(url, c.Sha), copy: false)
        : Task.CompletedTask;

    [RelayCommand]
    private Task CopyCommitLink() => History.SelectedCommit is { } c
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
        if (History.SelectedCommit is not { } commit || PromptBranchName is null)
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
        if (History.SelectedCommit is not { } commit)
        {
            return Task.CompletedTask;
        }

        return RunAsync(() => _git.RevertAsync(Repository.Path, commit.Sha), string.Format(Loc["Status_Reverted"], commit.ShortSha));
    }

    /// <summary>Merges the selected commit into the current branch.</summary>
    [RelayCommand]
    private Task MergeCommit()
    {
        if (History.SelectedCommit is not { } commit)
        {
            return Task.CompletedTask;
        }

        return RunAsync(() => _git.MergeAsync(Repository.Path, commit.Sha), string.Format(Loc["Status_Merged"], commit.ShortSha));
    }

    /// <summary>Rebases the current branch onto the selected commit (confirmed — it rewrites history).</summary>
    [RelayCommand]
    private async Task RebaseOnto()
    {
        if (History.SelectedCommit is not { } commit)
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
        if (History.SelectedCommit is not { } commit || PromptResetMode is null)
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

    /// <summary>Reloads status, branches and stashes from git. Never throws.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            // Status, branches, stashes and tags are independent reads, so fire them together and let
            // them run as concurrent git processes instead of one-after-another. RefreshBaseRefsAsync
            // stays sequential below because it reads the freshly-refreshed Branches list.
            var statusTask = _git.GetStatusAsync(Repository.Path);
            var branchesTask = _git.GetBranchesAsync(Repository.Path);
            var remoteBranchesTask = _git.GetRemoteBranchesAsync(Repository.Path);
            var stashesTask = _git.GetStashesAsync(Repository.Path);
            var tagsTask = _git.GetTagsAsync(Repository.Path);
            await Task.WhenAll(statusTask, branchesTask, remoteBranchesTask, stashesTask, tagsTask);

            var status = await statusTask;
            _lastStatusFingerprint = status.Fingerprint;   // what the watcher compares against

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
            Replace(Branches, await branchesTask);
            Replace(RemoteBranches, (await remoteBranchesTask).Select(n => new GitBranch { Name = n, IsRemote = true }));
            SelectedLocalBranch = Branches.FirstOrDefault(b => b.Name == branchName)
                ?? Branches.FirstOrDefault(b => b.IsCurrent);
            NarrowBranches();   // keep the Branch-flyout's filtered lists in sync with the refreshed data
            History.InvalidateContainment();   // branch tips / HEAD moved, so cached commit-containment is stale
            await RefreshBaseRefsAsync();
            await RefreshIdentityAsync();
            await RefreshRemotesAsync();

            Replace(Stashes, await stashesTask);
            HasStashes = Stashes.Count > 0;

            RebuildTags(await tagsTask);
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

    // ── Remotes ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Configured remotes (name + URL), shown in the manage-remotes dialog.</summary>
    public ObservableCollection<GitRemote> Remotes { get; } = [];

    /// <summary>No remote configured — the UI offers to add one instead of showing empty push/pull.</summary>
    [ObservableProperty]
    public partial bool HasNoRemote { get; set; }

    [ObservableProperty]
    public partial string NewRemoteName { get; set; } = "origin";

    [ObservableProperty]
    public partial string NewRemoteUrl { get; set; } = string.Empty;

    /// <summary>Reloads the remote list; refreshed when a repo opens and after add/remove.</summary>
    public async Task RefreshRemotesAsync()
    {
        try
        {
            Replace(Remotes, await _git.GetRemoteListAsync(Repository.Path));
        }
        catch (GitException)
        {
            Remotes.Clear();
        }

        HasNoRemote = Remotes.Count == 0;
    }

    [RelayCommand]
    private async Task AddRemote()
    {
        var name = NewRemoteName.Trim();
        var url = NewRemoteUrl.Trim();
        if (name.Length == 0 || url.Length == 0)
        {
            return;
        }

        await RunAsync(
            () => _git.AddRemoteAsync(Repository.Path, name, url),
            string.Format(Loc["Status_RemoteAdded"], name));

        NewRemoteUrl = string.Empty;
        await RefreshRemotesAsync();

        // A freshly-added remote should populate the ahead/behind counts right away.
        await CheckRemoteAsync();
    }

    [RelayCommand]
    private async Task RemoveRemote(GitRemote? remote)
    {
        if (remote is null)
        {
            return;
        }

        await RunAsync(
            () => _git.RemoveRemoteAsync(Repository.Path, remote.Name),
            string.Format(Loc["Status_RemoteRemoved"], remote.Name));

        await RefreshRemotesAsync();
    }

    /// <summary>Status as of the last reload; the watcher compares against it before doing any work.</summary>
    private string _lastStatusFingerprint = string.Empty;

    /// <summary>
    /// A change landed on disk from outside GitFlick — a commit from VS Code, a CLI checkout, an editor
    /// saving a file. Reloads from the working tree only: no fetch, because nothing about the remote
    /// changed and a network round-trip on every keystroke-triggered save would be absurd.
    ///
    /// This is the gap the remote-oriented <see cref="AutoSyncAsync"/> can't cover: staging and local
    /// edits never move ahead/behind/upstream, so it has nothing to react to.
    /// </summary>
    public async Task RefreshFromDiskAsync()
    {
        if (IsBusy)
        {
            return;   // our own command owns the view and refreshes when it finishes
        }

        // Look before rebuilding. The watcher can't tell a real edit from build output landing in
        // bin/obj, or from git rewriting its own index — and blindly reloading on each one both
        // thrashed the view and fed itself, since a reload touches the very files being watched.
        // Comparing the status first makes all of that free: no change, no work.
        GitStatus status;
        try
        {
            status = await _git.GetStatusAsync(Repository.Path);
        }
        catch (GitException)
        {
            return;   // mid-operation (an index.lock, say) — the next event will catch up
        }

        if (status.Fingerprint == _lastStatusFingerprint)
        {
            return;
        }

        // Something genuinely moved. Keep the commit the user is reading open across the reload —
        // a background refresh has no business closing the pane they're looking at.
        var openCommit = History.SelectedCommit?.Sha;

        await RefreshAsync();

        if (IsHistoryMode)
        {
            await History.LoadHistoryAsync();

            if (openCommit is not null)
            {
                History.SelectedCommit = History.Commits.FirstOrDefault(c => c.Sha == openCommit);
            }
        }
    }

    /// <summary>
    /// Periodic/on-focus auto-sync (the timer and window activation call this). Fetches in the
    /// background, then — only if the remote actually moved — reloads the file lists and history so new
    /// remote commits show without a manual refresh. Skips the reload when a command is running or
    /// nothing changed, so it never yanks the view out from under an active edit for no reason.
    /// </summary>
    public async Task AutoSyncAsync()
    {
        if (IsBusy || IsCheckingRemote)
        {
            return;
        }

        // Remember the sync position; the fetch updates it. A change means the remote (or our branch)
        // moved and the view is now stale.
        var before = (Ahead, Behind, Upstream);
        await CheckRemoteAsync();

        if (IsBusy)
        {
            return;   // a command started while we were fetching — leave it alone
        }

        if (before != (Ahead, Behind, Upstream))
        {
            await RefreshAsync();
            if (IsHistoryMode)
            {
                await History.LoadHistoryAsync();
            }
        }
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

        // Only clear the box on success (a failed commit keeps the message for the retry).
        if (await RunAsync(() => _git.CommitAsync(Repository.Path, message), Loc["Status_Committed"]))
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
        var committed = await RunAsync(
            async () =>
            {
                var staged = await _git.StageAllAsync(Repository.Path);
                return staged.Succeeded ? await _git.CommitAsync(Repository.Path, message) : staged;
            },
            Loc["Status_CommittedAll"]);

        if (committed)
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

        if (await RunAsync(() => _git.CommitAsync(Repository.Path, message, signOff: true), Loc["Status_CommittedSignedOff"]))
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
        var amended = await RunAsync(() => _git.CommitAmendAsync(Repository.Path, reworded ? message : null), Loc["Status_AmendedLast"]);

        if (reworded && amended)
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
        var amended = await RunAsync(
            async () =>
            {
                var staged = await _git.StageAllAsync(Repository.Path);
                return staged.Succeeded
                    ? await _git.CommitAmendAsync(Repository.Path, reworded ? message : null)
                    : staged;
            },
            Loc["Status_AmendedLast"]);

        if (reworded && amended)
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
            string.Format(Loc["Status_PulledFrom"], source.Branch, source.Remote));
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
            string.Format(Loc["Status_PushedTo"], BranchName, remote));
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
        if (SelectedBranch is not { } branch)
        {
            return Task.CompletedTask;
        }

        // A remote branch DWIMs to its local tracking branch: "origin/main" -> checkout "main", which
        // git creates tracking origin/main if no local branch of that name exists yet.
        if (branch.IsRemote)
        {
            var local = branch.Name[(branch.Name.IndexOf('/') + 1)..];
            return GuardedCheckout(local, string.Format(Loc["Status_SwitchedTo"], local));
        }

        return branch.IsCurrent
            ? Task.CompletedTask   // already on it
            : GuardedCheckout(branch.Name, string.Format(Loc["Status_SwitchedTo"], branch.Name));
    }

    [RelayCommand]
    private Task DeleteBranch()
    {
        // Local branches only, and never the checked-out one (git refuses that anyway).
        if (SelectedBranch is not { IsRemote: false, IsCurrent: false } branch)
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
        if (SelectedBranch is not { IsRemote: false } branch || PromptBranchName is null)
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
        if (SelectedBranch is not { IsRemote: false } branch || PromptPickRef is null)
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
    private Task UnsetUpstream() => SelectedBranch is { IsRemote: false } branch
        ? RunAsync(() => _git.UnsetUpstreamAsync(Repository.Path, branch.Name), Loc["Status_UnsetUpstream"])
        : Task.CompletedTask;

    /// <summary>
    /// Publish the selected local branch that has no upstream yet: push --set-upstream to a remote — the
    /// same flow the toolbar Push takes for an unpublished current branch, but for any picked branch.
    /// </summary>
    [RelayCommand]
    private async Task PublishBranch()
    {
        if (SelectedBranch is not { IsRemote: false, Upstream: null } branch)
        {
            return;
        }

        var remotes = await _git.GetRemotesAsync(Repository.Path);
        if (remotes.Count == 0)
        {
            StatusText = Loc["Status_NoRemotes"];
            return;
        }

        // One remote is the normal case; with several, ask which — same picker as "Push to…".
        var remote = remotes.Count == 1
            ? remotes[0]
            : await (PromptPushTarget?.Invoke(remotes, branch.Name) ?? Task.FromResult<string?>(null));
        if (remote is null)
        {
            return;   // cancelled
        }

        if (ConfirmPublishBranch is not null && !await ConfirmPublishBranch(branch.Name, remote))
        {
            return;
        }

        await RunAsync(
            () => _git.PublishBranchAsync(Repository.Path, remote, branch.Name, Progress()),
            string.Format(Loc["Status_PublishedBranch"], branch.Name, remote));
    }

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
    private Task ViewStash(StashEntry? entry)
    {
        if (entry is null)
        {
            return Task.CompletedTask;
        }

        // Drop any file/commit selection so the diff pane shows the stash's patch.
        SelectedUnstagedFile = null;
        SelectedStagedFile = null;
        ClearDiff();
        return LoadDiffAsync(entry.Description, () => _git.GetStashDiffAsync(Repository.Path, entry.Index));
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

    private Task ShowDiffAsync(GitStatusEntry entry, bool staged)
        => LoadDiffAsync(
            entry.Path,
            () => _git.GetDiffAsync(
                Repository.Path,
                entry.Path,
                staged,
                untracked: entry.Kind == GitChangeKind.Untracked));

    private int _diffLoadToken;

    /// <summary>
    /// Loads a diff into the pane with a staleness guard: each call supersedes the previous one, so a
    /// slower earlier load can't overwrite a faster later selection (last-writer-wins on rapid
    /// re-selection). Empty output shows the localized "no textual changes" placeholder; a git
    /// failure shows its message.
    /// </summary>
    internal async Task LoadDiffAsync(string path, Func<Task<string>> fetch)
    {
        var token = ++_diffLoadToken;
        DiffPath = path;
        DiffText = Loc["Diff_Loading"];

        try
        {
            var diff = await fetch();
            if (token != _diffLoadToken)
            {
                return;   // a newer load started while we were awaiting — drop this result
            }

            DiffText = diff.Trim().Length == 0 ? Loc["Diff_NoTextualChanges"] : diff;
        }
        catch (GitException ex)
        {
            if (token == _diffLoadToken)
            {
                DiffText = ex.Message;
            }
        }
    }

    internal void ClearDiff()
    {
        DiffPath = string.Empty;
        DiffText = string.Empty;
        History.ResetCommitFiles();
    }

    private IProgress<string> Progress() => new Progress<string>(line => StatusText = line);

    /// <summary>
    /// Runs one git operation with busy-state and error handling, then refreshes. Sets
    /// <see cref="StatusText"/> to the success message or git's own failure text, and returns whether
    /// the operation succeeded so callers can react (e.g. clear the commit box only on success).
    /// </summary>
    private async Task<bool> RunAsync(Func<Task<GitCommandResult>> operation, string successMessage)
    {
        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        StatusText = Loc["Status_Working"];

        var succeeded = false;
        try
        {
            var result = await operation();
            succeeded = result.Succeeded;
            StatusText = succeeded ? successMessage : result.FailureMessage;
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
                await History.LoadHistoryAsync();
            }
        }

        return succeeded;
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
