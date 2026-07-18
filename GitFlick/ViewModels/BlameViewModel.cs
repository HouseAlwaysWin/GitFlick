using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using GitFlick.Models;
using GitFlick.Services;

namespace GitFlick.ViewModels;

/// <summary>
/// A standalone blame view for one file: every line, attributed to the commit that last touched it,
/// optionally as of a given revision. Self-contained, like <see cref="CompareViewModel"/>.
/// </summary>
public partial class BlameViewModel : ObservableObject
{
    private readonly IGitService _git;
    private readonly string _repoPath;
    private readonly string? _rev;

    public BlameViewModel(IGitService git, string repoPath, string path, string? rev = null)
    {
        _git = git;
        _repoPath = repoPath;
        Path = path;
        _rev = rev;
    }

    private static LocalizationService Loc => LocalizationService.Instance;

    public string Path { get; }
    public string Title => string.Format(Loc["Blame_Title"], Path);

    public ObservableCollection<BlameLine> Lines { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    public async Task LoadAsync()
    {
        IsLoading = true;
        Error = null;

        try
        {
            var blame = await _git.GetBlameAsync(_repoPath, Path, _rev);
            Lines.Clear();
            foreach (var line in blame)
            {
                Lines.Add(line);
            }
        }
        catch (GitException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
