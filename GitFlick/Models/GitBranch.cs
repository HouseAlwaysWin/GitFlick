namespace GitFlick.Models;

public sealed record GitBranch
{
    public required string Name { get; init; }

    public bool IsCurrent { get; init; }

    public bool IsRemote { get; init; }

    public string? Upstream { get; init; }

    public int Ahead { get; init; }

    public int Behind { get; init; }
}
