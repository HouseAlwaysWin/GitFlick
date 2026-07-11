namespace GitFlick.Models;

/// <summary>One entry from <c>git stash list</c>, e.g. index 0 = <c>stash@{0}</c>.</summary>
public sealed record StashEntry(int Index, string Description);
