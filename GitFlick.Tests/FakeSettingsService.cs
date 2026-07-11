using GitFlick.Models;
using GitFlick.Services;

namespace GitFlick.Tests;

/// <summary>In-memory settings that counts saves, so tests can assert persistence happened.</summary>
internal sealed class FakeSettingsService : ISettingsService
{
    public AppSettings Current { get; } = new();

    public string FilePath => "<memory>";

    public int SaveCount { get; private set; }

    public void Load()
    {
    }

    public void Save() => SaveCount++;
}
