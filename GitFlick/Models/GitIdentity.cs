namespace GitFlick.Models;

/// <summary>
/// Who commits are authored as. Git resolves this per repo (a repo-local value beats the global
/// one), so <see cref="IsRepoOverride"/> records which level the effective value came from —
/// otherwise "Martin Wang" looks the same whether it applies everywhere or only here.
/// </summary>
public sealed record GitIdentity(string Name, string Email, bool IsRepoOverride)
{
    /// <summary>Neither level set one — git will refuse to commit until it has a name and email.</summary>
    public static readonly GitIdentity None = new(string.Empty, string.Empty, false);

    public bool IsConfigured => Name.Length > 0 && Email.Length > 0;

    public override string ToString() => IsConfigured ? $"{Name} <{Email}>" : string.Empty;
}

/// <summary>An identity the user keeps around to switch between (e.g. personal vs work).</summary>
public sealed class SavedIdentity
{
    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public override string ToString() => $"{Name} <{Email}>";
}
