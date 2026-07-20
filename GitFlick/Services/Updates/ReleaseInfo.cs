using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GitFlick.Services.Updates;

/// <summary>
/// One GitHub release, as returned by the <c>/releases</c> REST endpoint. Only the fields the updater
/// needs are mapped. GitFlick ships a single Windows artifact, so asset selection is Windows-only.
/// </summary>
public sealed class ReleaseInfo
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("assets")]
    public List<ReleaseAsset> Assets { get; set; } = [];

    /// <summary>Tag with the leading <c>v</c> stripped, e.g. <c>v1.2.3</c> → <c>1.2.3</c>.</summary>
    [JsonIgnore]
    public string NormalizedVersion => TagName.TrimStart('v');

    /// <summary>
    /// The downloadable app package: the <c>win-x64</c> <c>.zip</c> the release workflow publishes
    /// (<c>GitFlick_win-x64.zip</c>). Falls back to any <c>.zip</c> if the naming ever changes.
    /// </summary>
    public ReleaseAsset? GetPreferredZipAsset() =>
        Assets.Find(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                         && a.Name.Contains("win", StringComparison.OrdinalIgnoreCase))
        ?? Assets.Find(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    /// <summary>The <c>SHA256SUMS.txt</c> manifest the workflow publishes alongside the zip.</summary>
    public ReleaseAsset? GetChecksumAsset() =>
        Assets.Find(a => string.Equals(a.Name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
}

/// <summary>A single downloadable file attached to a release.</summary>
public sealed class ReleaseAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
