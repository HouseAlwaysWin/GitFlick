namespace GitFlick.Models;

/// <summary>
/// Which conflict-producing operation Git is paused in the middle of. Merge, cherry-pick, revert and
/// rebase all leave the same "unmerged paths, waiting to finish or abort" state (a
/// <see href="CONTEXT.md">conflicted operation</see>); this says which one, so the resolver knows the
/// right <c>--continue</c> / <c>--abort</c> verb and can label the banner.
/// </summary>
public enum ConflictOperation
{
    None,
    Merge,
    CherryPick,
    Revert,
    Rebase,
}
