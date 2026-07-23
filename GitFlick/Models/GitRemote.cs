namespace GitFlick.Models;

/// <summary>A configured remote: its name (e.g. "origin") and fetch URL.</summary>
public sealed record GitRemote(string Name, string Url)
{
    public override string ToString() => $"{Name}  {Url}";
}
