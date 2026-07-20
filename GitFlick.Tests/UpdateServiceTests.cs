using System.Collections.Generic;
using System.Linq;
using GitFlick.Services.Updates;

namespace GitFlick.Tests;

public class UpdateServiceTests
{
    // ---- Version comparison -------------------------------------------------------------------

    [Theory]
    [InlineData("1.2.3", "1.2.2", true)]
    [InlineData("1.2.3", "1.2.3", false)]
    [InlineData("1.2.2", "1.2.3", false)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("1.10.0", "1.9.0", true)]   // numeric, not lexical ("10" > "9")
    [InlineData("0.2.0", "0.1.9", true)]
    public void IsNewerVersion_compares_numerically(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsNewerVersion(candidate, current));
    }

    [Fact]
    public void IsNewerVersion_falls_back_to_ordinal_when_unparseable()
    {
        Assert.True(UpdateService.IsNewerVersion("beta2", "beta1"));
        Assert.False(UpdateService.IsNewerVersion("beta1", "beta2"));
    }

    [Fact]
    public void Instance_compares_release_against_its_current_version()
    {
        var svc = new UpdateService("1.2.0");

        Assert.True(svc.IsSameAsCurrent(new ReleaseInfo { TagName = "v1.2.0" }));
        Assert.True(svc.IsSameAsCurrent(new ReleaseInfo { TagName = "1.2.0" }));
        Assert.True(svc.IsNewerThanCurrent(new ReleaseInfo { TagName = "v1.3.0" }));
        Assert.False(svc.IsNewerThanCurrent(new ReleaseInfo { TagName = "v1.1.0" }));
    }

    // ---- Asset selection ----------------------------------------------------------------------

    [Fact]
    public void GetPreferredZipAsset_picks_the_win_zip()
    {
        var release = new ReleaseInfo
        {
            Assets =
            {
                new ReleaseAsset { Name = "SHA256SUMS.txt" },
                new ReleaseAsset { Name = "GitFlick_Setup_1.0.0.exe" },
                new ReleaseAsset { Name = "GitFlick_win-x64.zip" },
            },
        };

        Assert.Equal("GitFlick_win-x64.zip", release.GetPreferredZipAsset()?.Name);
    }

    [Fact]
    public void GetPreferredZipAsset_is_null_when_no_zip_present()
    {
        var release = new ReleaseInfo { Assets = { new ReleaseAsset { Name = "GitFlick_Setup.exe" } } };
        Assert.Null(release.GetPreferredZipAsset());
    }

    [Fact]
    public void GetChecksumAsset_matches_the_manifest_name_case_insensitively()
    {
        var release = new ReleaseInfo { Assets = { new ReleaseAsset { Name = "sha256sums.txt" } } };
        Assert.NotNull(release.GetChecksumAsset());
    }

    [Fact]
    public void NormalizedVersion_strips_leading_v()
    {
        Assert.Equal("1.2.3", new ReleaseInfo { TagName = "v1.2.3" }.NormalizedVersion);
        Assert.Equal("1.2.3", new ReleaseInfo { TagName = "1.2.3" }.NormalizedVersion);
    }

    // ---- Filter + sort ------------------------------------------------------------------------

    [Fact]
    public void FilterAndSortReleases_drops_drafts_prereleases_assetless_and_sorts_newest_first()
    {
        var releases = new List<ReleaseInfo>
        {
            WithZip("v1.0.0"),
            WithZip("v1.2.0"),
            WithZip("v1.10.0"),                                             // sorts above 1.2.0 numerically
            new() { TagName = "v2.0.0", Draft = true, Assets = { Zip() } }, // dropped: draft
            new() { TagName = "v2.1.0", Prerelease = true, Assets = { Zip() } }, // dropped: prerelease
            new() { TagName = "v3.0.0" },                                  // dropped: no asset
        };

        var result = UpdateService.FilterAndSortReleases(releases);

        Assert.Equal(new[] { "v1.10.0", "v1.2.0", "v1.0.0" }, result.Select(r => r.TagName).ToArray());
    }

    // ---- Checksum parsing ---------------------------------------------------------------------

    [Fact]
    public void ParseChecksum_returns_the_hash_for_the_named_file()
    {
        var hash = new string('a', 64);
        var manifest = $"{new string('b', 64)}  other.zip\n{hash}  GitFlick_win-x64.zip\n";

        Assert.Equal(hash, UpdateService.ParseChecksum(manifest, "GitFlick_win-x64.zip"));
        Assert.Null(UpdateService.ParseChecksum(manifest, "missing.zip"));
    }

    [Fact]
    public void ParseChecksum_tolerates_the_binary_star_marker()
    {
        var hash = new string('c', 64);
        var manifest = $"{hash} *GitFlick_win-x64.zip";

        Assert.Equal(hash, UpdateService.ParseChecksum(manifest, "GitFlick_win-x64.zip"));
    }

    // ---- Update script ------------------------------------------------------------------------

    [Fact]
    public void BuildUpdateScript_waits_on_the_pid_then_swaps_and_restarts()
    {
        var script = UpdateService.BuildUpdateScript(
            extractSourceDir: @"C:\Temp\up\extract",
            appDir: @"C:\Program Files\GitFlick",
            parentDir: @"C:\Temp\up",
            expectedExePath: @"C:\Program Files\GitFlick\GitFlick.exe",
            currentProcessId: 4242);

        Assert.Contains("PID eq 4242", script);            // waits for our process to exit
        Assert.Contains("robocopy", script);               // swaps the install
        Assert.Contains(@"C:\Program Files\GitFlick\GitFlick.exe", script);
        Assert.Contains(":rollback", script);              // has a rollback path
        Assert.Contains("start \"\"", script);             // restarts
    }

    private static ReleaseInfo WithZip(string tag) => new() { TagName = tag, Assets = { Zip() } };

    private static ReleaseAsset Zip() => new()
    {
        Name = "GitFlick_win-x64.zip",
        DownloadUrl = "https://example.test/GitFlick_win-x64.zip",
        Size = 1,
    };
}
