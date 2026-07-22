using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace GitFlick.Tests;

/// <summary>
/// A throwaway git repository in a temp folder for integration tests. Configures identity so
/// commits work, and cleans itself up (clearing read-only pack files first, which Windows
/// otherwise refuses to delete).
/// </summary>
internal sealed class TestRepo : IDisposable
{
    public string Path { get; }

    public TestRepo()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "GitFlickGitTests", System.IO.Path.GetRandomFileName());
        Directory.CreateDirectory(Path);

        Git("init", "--initial-branch=main");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "GitFlick Test");
        Git("config", "commit.gpgsign", "false");
        Git("config", "core.autocrlf", "false");
    }

    /// <summary>Writes a file (UTF-8, no BOM) relative to the repo root, creating directories.</summary>
    public void WriteFile(string relativePath, string content)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// Runs git for setup where a non-zero exit is the expected outcome — a conflicting <c>merge</c>
    /// exits 1 by design, and that is exactly the state some tests need to set up.
    /// </summary>
    public void GitAllowFail(params string[] args)
    {
        try
        {
            Git(args);
        }
        catch (InvalidOperationException)
        {
            // Expected: the caller wants the working tree in whatever state this left behind.
        }
    }

    /// <summary>Runs git directly for test setup (bypassing the class under test). Throws on failure.</summary>
    public string Git(params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed ({process.ExitCode}): {stderr}{stdout}");
        }

        return stdout;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                ClearReadOnly(new DirectoryInfo(Path));
                Directory.Delete(Path, recursive: true);
            }
        }
        catch
        {
            // Best effort: a locked temp folder is not worth failing a test over.
        }
    }

    private static void ClearReadOnly(DirectoryInfo dir)
    {
        foreach (var file in dir.GetFiles())
        {
            file.Attributes = FileAttributes.Normal;
        }

        foreach (var sub in dir.GetDirectories())
        {
            ClearReadOnly(sub);
        }
    }
}
