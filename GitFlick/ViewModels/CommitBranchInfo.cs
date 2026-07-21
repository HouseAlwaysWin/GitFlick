using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GitFlick.Models;

namespace GitFlick.ViewModels;

/// <summary>
/// Structured "where does this commit sit" for the graph-dot popup and the diff-pane line: the short
/// SHA, whether it's reachable from HEAD, and the branch chips containing it. Updated in place so an
/// open popup fills in once git answers.
/// </summary>
public sealed partial class CommitBranchInfo : ObservableObject
{
    private const int MaxChips = 10;   // an old commit is contained by everything; keep the card compact

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSha))]
    public partial string Sha { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool InHead { get; set; }

    [ObservableProperty]
    public partial bool IsLoaded { get; set; }

    [ObservableProperty]
    public partial bool HasBranches { get; set; }

    /// <summary>Branch chips (local + remote), capped, with a trailing "+N" chip when there are more.</summary>
    public ObservableCollection<string> Branches { get; } = [];

    public bool HasSha => !string.IsNullOrEmpty(Sha);

    /// <summary><paramref name="containment"/> null = still loading; otherwise fill HEAD status + chips.</summary>
    public void Update(string sha, CommitContainment? containment)
    {
        Sha = sha;
        IsLoaded = containment is not null;
        InHead = containment?.InHead ?? false;

        Branches.Clear();
        if (containment is not null)
        {
            var all = containment.Branches;
            for (var i = 0; i < all.Count && i < MaxChips; i++)
            {
                Branches.Add(all[i]);
            }

            if (all.Count > MaxChips)
            {
                Branches.Add($"+{all.Count - MaxChips}");
            }
        }

        HasBranches = Branches.Count > 0;
    }

    public void Clear()
    {
        Sha = string.Empty;
        IsLoaded = false;
        InHead = false;
        Branches.Clear();
        HasBranches = false;
    }
}
