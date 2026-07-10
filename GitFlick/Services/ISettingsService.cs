using GitFlick.Models;

namespace GitFlick.Services;

public interface ISettingsService
{
    AppSettings Current { get; }

    string FilePath { get; }

    void Load();

    void Save();
}
