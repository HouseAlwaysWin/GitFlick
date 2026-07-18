using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitFlick.Models;

namespace GitFlick.Services;

public sealed class GitService : IGitService
{
    private readonly string _gitPath;

    public GitCommandLog CommandLog { get; } = new();

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

    public async Task<string> GetDiffAsync(
        string repoPath,
        string path,
        bool staged,
        bool untracked = false,
        CancellationToken cancellationToken = default)
    {
        // An untracked file is in neither HEAD nor the index, so plain `git diff` prints
        // nothing. Diffing it against an empty file renders it as all-added. --no-index
        // exits 1 when the files differ, which here is the expected outcome, not a failure.
        var args = untracked
            ? new[] { "diff", "--no-index", "--", NullDevice, path }
            : staged
                ? ["diff", "--cached", "--", path]
                : new[] { "diff", "--", path };

        var result = await RunAsync(repoPath, args, null, cancellationToken).ConfigureAwait(false);

        if (result.Succeeded || (untracked && result.ExitCode == 1))
        {
            return result.StandardOutput;
        }

        throw new GitException($"git diff failed: {result.FailureMessage}");
    }

    /// <summary>Git accepts this on Windows too — it is git's own null path, not the OS's.</summary>
    private const string NullDevice = "/dev/null";

    public Task<GitCommandResult> StageAsync(string repoPath, string path, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["add", "--", path], null, cancellationToken);

    public Task<GitCommandResult> StageAllAsync(string repoPath, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["add", "-A"], null, cancellationToken);

    public Task<GitCommandResult> UnstageAsync(string repoPath, string path, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["reset", "--quiet", "--", path], null, cancellationToken);

    public Task<GitCommandResult> UnstageAllAsync(string repoPath, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["reset", "--quiet"], null, cancellationToken);

    public async Task<string> GetStagedDiffAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(repoPath, ["diff", "--cached"], null, cancellationToken).ConfigureAwait(false);
        return result.StandardOutput;
    }

    public Task<GitCommandResult> CommitAsync(string repoPath, string message, bool signOff = false, CancellationToken cancellationToken = default)
        => RunAsync(
            repoPath,
            signOff ? new[] { "commit", "-s", "-m", message } : new[] { "commit", "-m", message },
            null,
            cancellationToken);

    public Task<GitCommandResult> CommitAmendAsync(string repoPath, string? message, bool signOff = false, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "commit", "--amend" };
        if (signOff)
        {
            args.Add("-s");
        }

        // No message keeps the previous one (just folds in whatever is staged); a message rewords it.
        if (string.IsNullOrWhiteSpace(message))
        {
            args.Add("--no-edit");
        }
        else
        {
            args.Add("-m");
            args.Add(message);
        }

        return RunAsync(repoPath, args, null, cancellationToken);
    }

    public Task<GitCommandResult> UndoLastCommitAsync(string repoPath, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["reset", "--soft", "HEAD~1"], null, cancellationToken);

    public Task<GitCommandResult> DiscardPathAsync(string repoPath, string path, bool untracked, CancellationToken cancellationToken = default)
        => untracked
            // Untracked: the file (or directory) is new, so discarding means removing it.
            ? RunAsync(repoPath, ["clean", "-fd", "--", path], null, cancellationToken)
            // Tracked: revert the worktree change back to what's staged/committed.
            : RunAsync(repoPath, ["checkout", "--", path], null, cancellationToken);

    public async Task<GitCommandResult> DiscardAllAsync(string repoPath, bool includeUntracked, CancellationToken cancellationToken = default)
    {
        // Reset tracked files (staged and unstaged) back to HEAD.
        var reset = await RunAsync(repoPath, ["reset", "--hard", "HEAD"], null, cancellationToken).ConfigureAwait(false);
        if (!reset.Succeeded || !includeUntracked)
        {
            return reset;
        }

        // Optionally also remove untracked files and directories.
        return await RunAsync(repoPath, ["clean", "-fd"], null, cancellationToken).ConfigureAwait(false);
    }

    public Task<GitCommandResult> FetchAsync(string repoPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["fetch", "--progress"], progress, cancellationToken);

    public Task<GitCommandResult> FetchPruneAsync(string repoPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["fetch", "--prune", "--progress"], progress, cancellationToken);

    public Task<GitCommandResult> FetchAllAsync(string repoPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["fetch", "--all", "--progress"], progress, cancellationToken);

    public Task<GitCommandResult> PullAsync(string repoPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["pull", "--progress"], progress, cancellationToken);

    public Task<GitCommandResult> PullRebaseAsync(string repoPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["pull", "--rebase", "--progress"], progress, cancellationToken);

    public Task<GitCommandResult> PullFromAsync(string repoPath, string remote, string branch, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["pull", "--progress", remote, branch], progress, cancellationToken);

    public Task<GitCommandResult> PushAsync(string repoPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["push", "--progress"], progress, cancellationToken);

    public Task<GitCommandResult> PushToAsync(string repoPath, string remote, string branch, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["push", "--progress", remote, branch], progress, cancellationToken);

    public async Task<IReadOnlyList<string>> GetRemotesAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(repoPath, ["remote"], null, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return [];
        }

        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public async Task<IReadOnlyList<CommitInfo>> GetCommitsAsync(
        string repoPath,
        int maxCount = 300,
        bool firstParentOnly = false,
        string? pathFilter = null,
        CancellationToken cancellationToken = default)
    {
        // A repo with no commits has an unborn HEAD, and `git log ... HEAD` then fails outright
        // rather than printing nothing. Ask git directly instead of matching on its error text,
        // which is fragile and translated.
        var head = await RunAsync(repoPath, ["rev-parse", "--verify", "--quiet", "HEAD"], null, cancellationToken)
            .ConfigureAwait(false);

        if (!head.Succeeded)
        {
            return [];
        }

        var args = new List<string>
        {
            "log",
            "--no-show-signature",

            // The graph builder assumes a parent never appears before its child. Plain reverse-
            // chronological order can violate that under clock skew; --date-order cannot.
            "--date-order",
            "--decorate=full",
            "--format=" + CommitLogParser.Format,
            "--max-count=" + maxCount.ToString(CultureInfo.InvariantCulture),
        };

        if (firstParentOnly)
        {
            // One row per merge: the collapsed "what landed on this branch" view.
            args.Add("--first-parent");
            args.Add("HEAD");
        }
        else
        {
            // Every tip, so branches actually show up as lanes. Deliberately not --all, which
            // would drag in refs/stash and clutter the graph.
            args.Add("--branches");
            args.Add("--remotes");
            args.Add("--tags");
            args.Add("HEAD");
        }

        // Path filter (spec §5⑥/⑦): only commits that touched this pathspec. "--" separates it
        // from revisions so a path that looks like a ref can't be misread. git's own pathspec
        // rules apply, so a file, a folder, or a glob like *.cs all work.
        if (!string.IsNullOrWhiteSpace(pathFilter))
        {
            args.Add("--");
            args.Add(pathFilter);
        }

        var result = await RunAsync(repoPath, args, null, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new GitException($"git log failed: {result.FailureMessage}");
        }

        return CommitLogParser.Parse(result.StandardOutput);
    }

    public async Task<IReadOnlyList<string>> GetAllPathsAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        // Every path that ever appeared in history, across all refs — so a file that was later
        // renamed or deleted still shows up (under the name it had at the time). Fuels the
        // file-filter autocomplete. quotepath=false keeps CJK paths literal.
        var result = await RunAsync(
            repoPath,
            ["log", "--all", "--no-show-signature", "--pretty=format:", "--name-only"],
            null,
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return [];
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in result.StandardOutput.Split('\n'))
        {
            var path = raw.TrimEnd('\r');
            if (path.Length > 0)
            {
                paths.Add(path);
            }
        }

        return new List<string>(paths);
    }

    public async Task<string> GetCommitDiffAsync(string repoPath, string sha, CancellationToken cancellationToken = default)
    {
        // -m so merge commits produce a patch instead of nothing at all.
        var result = await RunAsync(
            repoPath,
            ["show", "--pretty=format:", "--patch", "-m", "--first-parent", sha],
            null,
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new GitException($"git show failed: {result.FailureMessage}");
        }

        return result.StandardOutput;
    }

    public async Task<string> GetCommitMessageAsync(string repoPath, string sha, CancellationToken cancellationToken = default)
    {
        // %B is the raw body — the complete commit message (subject + body) exactly as written.
        // Fetched on demand (the history list only carries the subject), so viewing it never fails hard.
        var result = await RunAsync(
            repoPath,
            ["log", "-1", "--no-show-signature", "--format=%B", sha],
            null,
            cancellationToken).ConfigureAwait(false);

        return result.Succeeded ? result.StandardOutput.TrimEnd('\r', '\n') : string.Empty;
    }

    public async Task<IReadOnlyList<CommitFileEntry>> GetCommitFilesAsync(string repoPath, string sha, CancellationToken cancellationToken = default)
    {
        // --name-status: one "M\tpath" line per file. --no-renames keeps it to a single tab
        // (a rename would otherwise be "R100\told\tnew"). -m --first-parent matches the patch below.
        var result = await RunAsync(
            repoPath,
            ["show", "--name-status", "--no-renames", "--format=", "-m", "--first-parent", sha],
            null,
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new GitException($"git show --name-status failed: {result.FailureMessage}");
        }

        var files = new List<CommitFileEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in result.StandardOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var tab = line.IndexOf('\t');
            if (tab <= 0)
            {
                continue;
            }

            var path = line[(tab + 1)..];
            if (path.Length > 0 && seen.Add(path))
            {
                files.Add(new CommitFileEntry(path, line[..tab]));
            }
        }

        return files;
    }

    public async Task<string> GetCommitFileDiffAsync(string repoPath, string sha, string path, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            repoPath,
            ["show", "--format=", "--patch", "-m", "--first-parent", sha, "--", path],
            null,
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new GitException($"git show (file) failed: {result.FailureMessage}");
        }

        return result.StandardOutput;
    }

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

    public Task<GitCommandResult> CherryPickAsync(string repoPath, string sha, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["cherry-pick", sha], null, cancellationToken);

    public Task<GitCommandResult> CreateTagAsync(string repoPath, string name, string sha, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["tag", name, sha], null, cancellationToken);

    public Task<GitCommandResult> CreateBranchAtAsync(string repoPath, string name, string sha, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["branch", name, sha], null, cancellationToken);

    public Task<GitCommandResult> RevertAsync(string repoPath, string sha, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["revert", "--no-edit", sha], null, cancellationToken);

    public Task<GitCommandResult> RebaseOntoAsync(string repoPath, string sha, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["rebase", sha], null, cancellationToken);

    public Task<GitCommandResult> ResetToAsync(string repoPath, string sha, GitResetMode mode, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["reset", mode switch
        {
            GitResetMode.Soft => "--soft",
            GitResetMode.Hard => "--hard",
            _ => "--mixed",
        }, sha], null, cancellationToken);

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
        // Log exactly what we ran (minus the encoding -c overrides, which are just noise). The
        // finally records every outcome — success, non-zero exit, cancellation, or a start failure.
        var command = FormatCommand(args);
        var stopwatch = Stopwatch.StartNew();
        var succeeded = false;

        try
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

            // A GUI app must never block on git's interactive credential prompt — a background fetch
            // would hang forever. Fail fast instead; the OS credential helper still serves cached creds.
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

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

            var result = new GitCommandResult(process.ExitCode, stdout, stderr);
            succeeded = result.Succeeded;
            return result;
        }
        finally
        {
            stopwatch.Stop();
            CommandLog.Record(new GitCommandLogEntry(command, succeeded, stopwatch.ElapsedMilliseconds, DateTime.Now));
        }
    }

    /// <summary>Renders the invocation as a copy-pasteable command line, quoting args with spaces.</summary>
    private static string FormatCommand(IReadOnlyList<string> args)
    {
        var builder = new StringBuilder("git");

        foreach (var arg in args)
        {
            builder.Append(' ');
            if (arg.Length == 0 || arg.Contains(' '))
            {
                builder.Append('"').Append(arg).Append('"');
            }
            else
            {
                builder.Append(arg);
            }
        }

        return builder.ToString();
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
