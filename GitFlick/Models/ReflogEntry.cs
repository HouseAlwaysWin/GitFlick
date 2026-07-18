using System;

namespace GitFlick.Models;

/// <summary>One entry from <c>git reflog</c> — a recorded move of HEAD.</summary>
public sealed record ReflogEntry(string Selector, string Sha, string Description, DateTimeOffset When)
{
    public string ShortSha => Sha.Length >= 7 ? Sha[..7] : Sha;

    public string WhenDisplay => When.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
}
