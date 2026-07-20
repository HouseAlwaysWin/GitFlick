using System;
using System.Diagnostics;
using System.IO;

namespace GitFlick.Services.Updates;

/// <summary>Locates the running executable so the updater can replace it in place.</summary>
internal static class RuntimePathProvider
{
    public static string GetExecutablePath() =>
        Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? AppContext.BaseDirectory;

    public static string GetExecutableDirectory()
    {
        var executablePath = GetExecutablePath();
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return AppContext.BaseDirectory;
        }

        var directory = Path.GetDirectoryName(executablePath);
        return string.IsNullOrWhiteSpace(directory) ? AppContext.BaseDirectory : directory;
    }
}
