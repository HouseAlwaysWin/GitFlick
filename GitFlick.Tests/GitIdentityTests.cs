using System.Text.Json;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.Services;

namespace GitFlick.Tests;

/// <summary>
/// The commit identity: git resolves repo-local over global, and the app has to report which level
/// the effective value came from so "global" and "just here" don't look the same.
/// </summary>
public class GitIdentityTests
{
    private readonly GitService _git = new();

    [Fact]
    public async Task A_repo_local_identity_wins_and_is_reported_as_an_override()
    {
        using var repo = new TestRepo();

        await _git.SetIdentityAsync(repo.Path, "Repo Person", "repo@example.com", global: false);

        var identity = await _git.GetIdentityAsync(repo.Path);

        Assert.Equal("Repo Person", identity.Name);
        Assert.Equal("repo@example.com", identity.Email);
        Assert.True(identity.IsRepoOverride);
        Assert.True(identity.IsConfigured);
    }

    [Fact]
    public async Task Clearing_the_override_falls_back_to_the_inherited_identity()
    {
        using var repo = new TestRepo();
        await _git.SetIdentityAsync(repo.Path, "Repo Person", "repo@example.com", global: false);
        Assert.True((await _git.GetIdentityAsync(repo.Path)).IsRepoOverride);

        await _git.ClearRepoIdentityAsync(repo.Path);

        var identity = await _git.GetIdentityAsync(repo.Path);
        Assert.False(identity.IsRepoOverride);
        Assert.NotEqual("Repo Person", identity.Name);   // whatever global says, just not the override
    }

    [Fact]
    public async Task Clearing_a_repo_that_never_had_an_override_is_not_an_error()
    {
        using var repo = new TestRepo();

        // git config --unset exits 5 for a missing key; that's "already as requested", not a failure.
        var result = await _git.ClearRepoIdentityAsync(repo.Path);

        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Fact]
    public void Saved_identities_survive_the_settings_round_trip()
    {
        var settings = new AppSettings
        {
            SavedIdentities =
            [
                new SavedIdentity { Name = "Martin Wang", Email = "martin@example.com" },
                new SavedIdentity { Name = "Work Martin", Email = "martin@work.example" },
            ],
        };

        var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
        var restored = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);

        Assert.NotNull(restored);
        Assert.Equal(
            settings.SavedIdentities.Select(i => i.ToString()),
            restored.SavedIdentities.Select(i => i.ToString()));
    }
}
