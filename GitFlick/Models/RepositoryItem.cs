namespace GitFlick.Models;

/// <summary>A pinned repository. Only the path is persisted; the name is derived.</summary>
public sealed record RepositoryItem(string Name, string Path)
{
    /// <summary>Accessibility tools read this for the list item, so keep it to the name.</summary>
    public override string ToString() => Name;
}
