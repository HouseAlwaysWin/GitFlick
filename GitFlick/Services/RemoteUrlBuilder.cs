using System;
using System.Linq;

namespace GitFlick.Services;

/// <summary>The web base of a remote plus its provider flavour.</summary>
public sealed record RemoteWebInfo(string WebBase, bool IsGitLab);

/// <summary>
/// Turns a git remote URL into web links for a commit, file, or branch. Handles scp-style
/// (<c>git@host:owner/repo.git</c>), <c>ssh://</c> and <c>https://</c> forms. GitLab paths use the
/// <c>/-/</c> infix; every other host defaults to the GitHub scheme. Pure and side-effect-free.
/// </summary>
public static class RemoteUrlBuilder
{
    public static RemoteWebInfo? Parse(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        var url = remoteUrl.Trim();
        string host;
        string path;

        var hasScheme = url.Contains("://", StringComparison.Ordinal);
        if (!hasScheme && url.Contains(':') && !url.Contains(":\\", StringComparison.Ordinal))
        {
            // scp-style: [user@]host:owner/repo(.git)
            var at = url.IndexOf('@');
            var afterUser = at >= 0 ? url[(at + 1)..] : url;
            var colon = afterUser.IndexOf(':');
            if (colon < 0)
            {
                return null;
            }

            host = afterUser[..colon];
            path = afterUser[(colon + 1)..];
        }
        else if (hasScheme)
        {
            // ssh://git@host[:port]/owner/repo(.git)  |  https://[user@]host/owner/repo(.git)
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return null;
            }

            host = uri.Host;
            path = uri.AbsolutePath;
        }
        else
        {
            return null;   // a local path or something without a web home
        }

        host = host.Trim();
        path = path.Trim('/', ' ');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        if (host.Length == 0 || path.Length == 0)
        {
            return null;
        }

        var isGitLab = host.Contains("gitlab", StringComparison.OrdinalIgnoreCase);
        return new RemoteWebInfo($"https://{host}/{path}", isGitLab);
    }

    public static string? Commit(string? remoteUrl, string sha) =>
        Parse(remoteUrl) is { } info ? $"{info.WebBase}/{(info.IsGitLab ? "-/commit" : "commit")}/{sha}" : null;

    public static string? Branch(string? remoteUrl, string branch) =>
        Parse(remoteUrl) is { } info ? $"{info.WebBase}/{(info.IsGitLab ? "-/tree" : "tree")}/{EncodeRef(branch)}" : null;

    public static string? File(string? remoteUrl, string reference, string path, int? line = null)
    {
        if (Parse(remoteUrl) is not { } info)
        {
            return null;
        }

        var seg = info.IsGitLab ? "-/blob" : "blob";
        var url = $"{info.WebBase}/{seg}/{EncodeRef(reference)}/{EncodePath(path)}";
        return line is { } n ? $"{url}#L{n}" : url;
    }

    // Keep the '/' separators (branch names and paths use them as structure), escape each segment.
    private static string EncodeRef(string reference) =>
        string.Join('/', reference.Split('/').Select(Uri.EscapeDataString));

    private static string EncodePath(string path) =>
        string.Join('/', path.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
}
