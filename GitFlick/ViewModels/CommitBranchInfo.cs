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

    // The card lays out on ONE line, so every element is a sibling and gates itself — these fold the
    // "already loaded" check into the HEAD chips instead of relying on a wrapping container.

    /// <summary>Loaded and reachable from HEAD — shows the green chip.</summary>
    [ObservableProperty]
    public partial bool ShowInHead { get; set; }

    /// <summary>Loaded and not reachable from HEAD — shows the grey chip.</summary>
    [ObservableProperty]
    public partial bool ShowNotInHead { get; set; }

    [ObservableProperty]
    public partial bool HasBranches { get; set; }

    /// <summary>The branch this commit belongs to when no ref points exactly at it (git name-rev).</summary>
    [ObservableProperty]
    public partial string NearestBranch { get; set; } = string.Empty;

    /// <summary>Show the branch-lineage chip: loaded, no exact refs, but a branch was resolved.</summary>
    [ObservableProperty]
    public partial bool HasNearest { get; set; }

    /// <summary><see cref="NearestBranch"/> is the branch it was merged from (vs the line it sits on).</summary>
    [ObservableProperty]
    public partial bool NearestIsMerge { get; set; }

    /// <summary>Loaded, but nothing points here and nothing reaches it — a truly detached commit.</summary>
    [ObservableProperty]
    public partial bool HasNoRef { get; set; }

    /// <summary>
    /// Refs pointing at this commit, capped. Carries each ref's kind so the chips can be coloured the
    /// way the commit rows are — "main" and "origin/main" read identically otherwise.
    /// </summary>
    public ObservableCollection<GitRef> Branches { get; } = [];

    /// <summary>"+N" for refs past the cap. Kept out of <see cref="Branches"/>: it isn't a ref.</summary>
    [ObservableProperty]
    public partial string OverflowText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasOverflow { get; set; }

    public bool HasSha => !string.IsNullOrEmpty(Sha);

    /// <summary><paramref name="containment"/> null = still loading; otherwise fill HEAD status + chips.</summary>
    public void Update(string sha, CommitContainment? containment)
    {
        Sha = sha;
        IsLoaded = containment is not null;
        InHead = containment?.InHead ?? false;
        ShowInHead = IsLoaded && InHead;
        ShowNotInHead = IsLoaded && !InHead;

        Branches.Clear();
        OverflowText = string.Empty;
        HasOverflow = false;

        if (containment is not null)
        {
            var all = containment.Branches;
            for (var i = 0; i < all.Count && i < MaxChips; i++)
            {
                Branches.Add(all[i]);
            }

            if (all.Count > MaxChips)
            {
                OverflowText = $"+{all.Count - MaxChips}";
                HasOverflow = true;
            }
        }

        HasBranches = Branches.Count > 0;
        NearestBranch = containment?.NearestBranch ?? string.Empty;
        NearestIsMerge = containment?.NearestIsMerge ?? false;
        HasNearest = IsLoaded && !HasBranches && NearestBranch.Length > 0;
        HasNoRef = IsLoaded && !HasBranches && !HasNearest;
    }

    public void Clear()
    {
        Sha = string.Empty;
        IsLoaded = false;
        InHead = false;
        ShowInHead = false;
        ShowNotInHead = false;
        Branches.Clear();
        HasBranches = false;
        OverflowText = string.Empty;
        HasOverflow = false;
        NearestBranch = string.Empty;
        NearestIsMerge = false;
        HasNearest = false;
        HasNoRef = false;
    }
}
