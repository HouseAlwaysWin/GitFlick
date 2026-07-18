using System;
using System.Linq;

namespace GitFlick.Models;

/// <summary>One line of <c>git blame</c> output: which commit last touched it, and the line itself.</summary>
public sealed record BlameLine(
    string Sha,
    string Author,
    DateTimeOffset When,
    string Summary,
    int LineNumber,
    string Content)
{
    /// <summary>The working-tree line git couldn't attribute yet (all-zero sha).</summary>
    public bool IsUncommitted => Sha.Length == 0 || Sha.All(c => c == '0');

    public string ShortSha => IsUncommitted ? "•••••••" : (Sha.Length >= 7 ? Sha[..7] : Sha);

    /// <summary>Author date in the viewer's local day; blank for uncommitted lines.</summary>
    public string WhenDisplay => IsUncommitted ? string.Empty : When.LocalDateTime.ToString("yyyy-MM-dd");

    /// <summary>What the tooltip shows: sha + summary (or a placeholder for uncommitted lines).</summary>
    public string Tooltip => IsUncommitted ? Summary : $"{ShortSha}  {Summary}";
}
