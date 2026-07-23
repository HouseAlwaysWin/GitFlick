using System.Collections.Generic;
using System.Collections.ObjectModel;
using GitFlick.Models;
using GitFlick.Services;

namespace GitFlick.ViewModels;

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
}
