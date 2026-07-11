using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitFlick.Models;

namespace GitFlick.Services;

public sealed class GitService : IGitService
{
    private readonly string _gitPath;

    /// <param name="gitExecutablePath">
    /// An explicit path from settings, or null/empty to resolve "git" on PATH.
    /// </param>
    public GitService(string? gitExecutablePath = null)
    {
        _gitPath = string.IsNullOrWhiteSpace(gitExecutablePath) ? "git" : gitExecutablePath;
    }

    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RunAsync(null, ["--version"], null, cancellationToken).ConfigureAwait(false);
            return result.Succeeded ? result.StandardOutput.Trim() : null;
        }
        catch (GitException)
        {
            return null;
        }
    }

    public async Task<bool> IsRepositoryAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RunAsync(path, ["rev-parse", "--is-inside-work-tree"], null, cancellationToken)
                .ConfigureAwait(false);
            return result.Succeeded && result.StandardOutput.Trim() == "true";
        }
        catch (GitException)
        {
            return false;
        }
    }

    public async Task<GitStatus> GetStatusAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(repoPath, ["status", "--porcelain=v2", "--branch"], null, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new GitException($"git status failed: {result.FailureMessage}");
        }

        return PorcelainV2Parser.Parse(result.StandardOutput);
    }

    public Task<GitCommandResult> StageAsync(string repoPath, string path, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["add", "--", path], null, cancellationToken);

    public Task<GitCommandResult> StageAllAsync(string repoPath, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["add", "-A"], null, cancellationToken);

    public Task<GitCommandResult> UnstageAsync(string repoPath, string path, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["reset", "--quiet", "--", path], null, cancellationToken);

    public Task<GitCommandResult> UnstageAllAsync(string repoPath, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["reset", "--quiet"], null, cancellationToken);

    public Task<GitCommandResult> CommitAsync(string repoPath, string message, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["commit", "-m", message], null, cancellationToken);

    public Task<GitCommandResult> FetchAsync(string repoPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["fetch", "--progress"], progress, cancellationToken);

    public Task<GitCommandResult> PullAsync(string repoPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["pull", "--progress"], progress, cancellationToken);

    public Task<GitCommandResult> PushAsync(string repoPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["push", "--progress"], progress, cancellationToken);

    public async Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        // NUL-separated fields, one local ref per line. %(HEAD) is "*" for the current branch.
        const string format = "%(HEAD)%00%(refname:short)%00%(upstream:short)%00%(upstream:track)";
        var result = await RunAsync(repoPath, ["for-each-ref", "--format=" + format, "refs/heads"], null, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new GitException($"git for-each-ref failed: {result.FailureMessage}");
        }

        var branches = new List<GitBranch>();

        foreach (var raw in result.StandardOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var f = line.Split('\0');
            if (f.Length < 4)
            {
                continue;
            }

            var (ahead, behind) = ParseTrack(f[3]);
            branches.Add(new GitBranch
            {
                Name = f[1],
                IsCurrent = f[0] == "*",
                IsRemote = false,
                Upstream = f[2].Length == 0 ? null : f[2],
                Ahead = ahead,
                Behind = behind,
            });
        }

        return branches;
    }

    public Task<GitCommandResult> CheckoutAsync(string repoPath, string branch, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["checkout", branch], null, cancellationToken);

    public Task<GitCommandResult> CreateBranchAsync(string repoPath, string name, bool checkout = true, CancellationToken cancellationToken = default)
        => checkout
            ? RunAsync(repoPath, ["checkout", "-b", name], null, cancellationToken)
            : RunAsync(repoPath, ["branch", name], null, cancellationToken);

    public Task<GitCommandResult> DeleteBranchAsync(string repoPath, string name, bool force = false, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["branch", force ? "-D" : "-d", name], null, cancellationToken);

    public Task<GitCommandResult> MergeAsync(string repoPath, string branch, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["merge", branch], null, cancellationToken);

    public async Task<IReadOnlyList<StashEntry>> GetStashesAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        // "stash@{0}\0<subject>" per line.
        var result = await RunAsync(repoPath, ["stash", "list", "--format=%gd%x00%gs"], null, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new GitException($"git stash list failed: {result.FailureMessage}");
        }

        var stashes = new List<StashEntry>();

        foreach (var raw in result.StandardOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var sep = line.IndexOf('\0');
            var selector = sep >= 0 ? line[..sep] : line;
            var description = sep >= 0 ? line[(sep + 1)..] : string.Empty;

            if (TryParseStashIndex(selector, out var index))
            {
                stashes.Add(new StashEntry(index, description));
            }
        }

        return stashes;
    }

    public Task<GitCommandResult> StashPushAsync(string repoPath, string? message = null, CancellationToken cancellationToken = default)
        => string.IsNullOrEmpty(message)
            ? RunAsync(repoPath, ["stash", "push"], null, cancellationToken)
            : RunAsync(repoPath, ["stash", "push", "-m", message], null, cancellationToken);

    public Task<GitCommandResult> StashPopAsync(string repoPath, int index = 0, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["stash", "pop", $"stash@{{{index}}}"], null, cancellationToken);

    /// <summary>
    /// Every git invocation funnels through here. Prepends the two config overrides that make
    /// CJK output survive (spec §4.1/§4.2), runs in the given working directory, decodes both
    /// streams as UTF-8, and honours cancellation by killing the process tree.
    /// </summary>
    private async Task<GitCommandResult> RunAsync(
        string? workingDirectory,
        IReadOnlyList<string> args,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _gitPath,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // §4.1: never octal-escape non-ASCII paths. §4.2: force UTF-8 for log/message output.
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.quotepath=false");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("i18n.logOutputEncoding=UTF-8");

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            throw new GitException(
                $"Could not start git ('{_gitPath}'). Is Git installed and on PATH?", ex);
        }

        // Read both streams concurrently to avoid the classic pipe-buffer deadlock, and drain
        // them fully before reading exit code.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = ReadStderrAsync(process.StandardError, progress, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return new GitCommandResult(process.ExitCode, stdout, stderr);
    }

    private static async Task<string> ReadStderrAsync(
        StreamReader reader, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        string? line;

        // git writes fetch/push/pull progress to stderr; surface it line by line as it arrives.
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            builder.AppendLine(line);
            progress?.Report(line);
        }

        return builder.ToString();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Process already gone or not killable; nothing useful to do.
        }
    }

    private static (int ahead, int behind) ParseTrack(string track)
    {
        var ahead = 0;
        var behind = 0;
        ExtractNumberAfter(track, "ahead ", ref ahead);
        ExtractNumberAfter(track, "behind ", ref behind);
        return (ahead, behind);
    }

    // Reads the integer that follows <key> in strings like "[ahead 2, behind 1]".
    private static void ExtractNumberAfter(string text, string key, ref int value)
    {
        var start = text.IndexOf(key, StringComparison.Ordinal);
        if (start < 0)
        {
            return;
        }

        start += key.Length;
        var end = start;
        while (end < text.Length && char.IsDigit(text[end]))
        {
            end++;
        }

        if (end > start)
        {
            value = int.Parse(text.AsSpan(start, end - start));
        }
    }

    private static bool TryParseStashIndex(string selector, out int index)
    {
        // "stash@{3}" -> 3
        index = 0;
        var open = selector.IndexOf('{');
        var close = selector.IndexOf('}');
        return open >= 0 && close > open
            && int.TryParse(selector.AsSpan(open + 1, close - open - 1), out index);
    }
}
