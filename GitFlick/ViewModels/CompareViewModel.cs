using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using GitFlick.Models;
using GitFlick.Services;

namespace GitFlick.ViewModels;

/// <summary>
/// A standalone "compare two refs" view: the commits in <c>compare</c> but not <c>base</c>, the
/// files that differ, and the per-file range diff. Self-contained so it never entangles the
/// history pipeline in two-ref state.
/// </summary>
public partial class CompareViewModel : ObservableObject
{
    private readonly IGitService _git;
    private readonly string _repoPath;

    public CompareViewModel(IGitService git, string repoPath, string baseRef, string compareRef)
    {
        _git = git;
        _repoPath = repoPath;
        BaseRef = baseRef;
        CompareRef = compareRef;
    }

    private static LocalizationService Loc => LocalizationService.Instance;

    public string BaseRef { get; }
    public string CompareRef { get; }
    public string Title => string.Format(Loc["Compare_Title"], BaseRef, CompareRef);

    public ObservableCollection<CommitInfo> Commits { get; } = [];
    public ObservableCollection<CommitFileEntry> Files { get; } = [];

    [ObservableProperty]
    public partial bool HasCommits { get; set; }

    [ObservableProperty]
    public partial CommitFileEntry? SelectedFile { get; set; }

    [ObservableProperty]
    public partial string DiffText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DiffPath { get; set; } = string.Empty;

    public async Task LoadAsync()
    {
        try
        {
            var commits = await _git.GetCommitsBetweenAsync(_repoPath, BaseRef, CompareRef);
            Commits.Clear();
            foreach (var commit in commits)
            {
                Commits.Add(commit);
            }
            HasCommits = Commits.Count > 0;

            var files = await _git.GetDiffFilesAsync(_repoPath, BaseRef, CompareRef);
            Files.Clear();
            foreach (var file in files)
            {
                Files.Add(file);
            }
        }
        catch (GitException ex)
        {
            DiffText = ex.Message;
        }
    }

    partial void OnSelectedFileChanged(CommitFileEntry? value)
    {
        if (value is not null)
        {
            _ = ShowFileDiffAsync(value);
        }
    }

    private async Task ShowFileDiffAsync(CommitFileEntry file)
    {
        DiffPath = file.Path;
        DiffText = Loc["Diff_Loading"];

        try
        {
            var diff = await _git.GetRefRangeFileDiffAsync(_repoPath, BaseRef, CompareRef, file.Path);
            DiffText = diff.Trim().Length == 0 ? Loc["Diff_NoTextualChanges"] : diff;
        }
        catch (GitException ex)
        {
            DiffText = ex.Message;
        }
    }
}
