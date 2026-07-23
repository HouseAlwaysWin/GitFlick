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

    public Task<GitCommandResult> PublishBranchAsync(string repoPath, string remote, string branch, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["push", "--progress", "--set-upstream", remote, branch], progress, cancellationToken);

    public async Task<GitIdentity> GetIdentityAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        // Plain "git config" run inside the repo already resolves local-over-global, so this is the
        // effective author. Which level it came from needs a separate --local probe: that exits
        // non-zero when the key isn't set there, which is how we tell "overridden here" from
        // "inherited from global".
        var name = await ConfigValueAsync(repoPath, ["config", "user.name"], cancellationToken).ConfigureAwait(false);
        var email = await ConfigValueAsync(repoPath, ["config", "user.email"], cancellationToken).ConfigureAwait(false);

        var localName = await ConfigValueAsync(repoPath, ["config", "--local", "--get", "user.name"], cancellationToken).ConfigureAwait(false);
        var localEmail = await ConfigValueAsync(repoPath, ["config", "--local", "--get", "user.email"], cancellationToken).ConfigureAwait(false);

        return new GitIdentity(name, email, localName.Length > 0 || localEmail.Length > 0);
    }

    /// <summary>
    /// One config read. <c>git config &lt;key&gt;</c> exits 1 for a key that simply isn't set, which is a
    /// legitimate empty answer; any other non-zero exit is a real failure and must not be reported as
    /// "no identity configured" — that would tell the user they have none when they do.
    /// </summary>
    private async Task<string> ConfigValueAsync(string repoPath, string[] args, CancellationToken cancellationToken)
    {
        var result = await RunAsync(repoPath, args, null, cancellationToken).ConfigureAwait(false);

        if (result.Succeeded)
        {
            return result.StandardOutput.Trim();
        }

        return result.ExitCode == 1
            ? string.Empty
            : throw new GitException($"git {string.Join(' ', args)} failed: {result.FailureMessage}");
    }

    public async Task<GitCommandResult> SetIdentityAsync(
        string repoPath, string name, string email, bool global, CancellationToken cancellationToken = default)
    {
        string[] scope = global ? ["--global"] : ["--local"];

        var setName = await RunAsync(repoPath, ["config", .. scope, "user.name", name], null, cancellationToken).ConfigureAwait(false);
        if (!setName.Succeeded)
        {
            return setName;
        }

        return await RunAsync(repoPath, ["config", .. scope, "user.email", email], null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitCommandResult> ClearRepoIdentityAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        // --unset exits 5 when the key isn't there. That's "already how the caller wants it", not a
        // failure, so only a real error is reported.
        var name = await RunAsync(repoPath, ["config", "--local", "--unset", "user.name"], null, cancellationToken).ConfigureAwait(false);
        var email = await RunAsync(repoPath, ["config", "--local", "--unset", "user.email"], null, cancellationToken).ConfigureAwait(false);

        if (!name.Succeeded && name.ExitCode != 5)
        {
            return name;
        }

        return email.Succeeded || email.ExitCode == 5
            ? new GitCommandResult(0, string.Empty, string.Empty)
            : email;
    }

    public async Task<IReadOnlyList<string>> GetRemotesAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(repoPath, ["remote"], null, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return [];
        }

        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public async Task<string?> GetRemoteUrlAsync(string repoPath, string remote, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(repoPath, ["remote", "get-url", remote], null, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return null;
        }

        var url = result.StandardOutput.Trim();
        return url.Length > 0 ? url : null;
    }

    public async Task<IReadOnlyList<CommitInfo>> GetCommitsAsync(
        string repoPath,
        int maxCount = 300,
        bool firstParentOnly = false,
        string? pathFilter = null,
        string? contentSearch = null,
        bool mergesOnly = false,
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

        // Pickaxe: only commits that changed the number of occurrences of this string (git log -S).
        if (!string.IsNullOrWhiteSpace(contentSearch))
        {
            args.Add("-S" + contentSearch);
        }

        // Merges only: just the merge commits — "what merges/PRs landed", the complement of
        // --first-parent's collapse. Not parent-closed, so the caller hides the lane graph.
        if (mergesOnly)
        {
            args.Add("--merges");
        }

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
            // --full-history keeps merge commits that touched the file, which git's default history
            // simplification would otherwise prune — so a file's merges show up, not just linear edits.
            args.Add("--full-history");
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

    public async Task<IReadOnlyList<BlameLine>> GetBlameAsync(string repoPath, string path, string? rev = null, CancellationToken cancellationToken = default)
    {
        // --porcelain gives per-line commit + author/summary; rev blames the file as of that commit,
        // otherwise the working tree (uncommitted lines carry an all-zero sha).
        var args = new List<string> { "blame", "--porcelain" };
        if (!string.IsNullOrEmpty(rev))
        {
            args.Add(rev);
        }
        args.Add("--");
        args.Add(path);

        var result = await RunAsync(repoPath, args, null, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new GitException($"git blame failed: {result.FailureMessage}");
        }

        return BlameParser.Parse(result.StandardOutput);
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

        return ParseNameStatus(result.StandardOutput);
    }

    public async Task<CommitContainment> GetCommitContainmentAsync(string repoPath, string sha, CancellationToken cancellationToken = default)
    {
        // Refs (local + remote branches, and tags) that point AT this commit — its decoration labels,
        // not everything that descends from it. --contains would list every branch forked after an old
        // trunk commit (dozens of them); --points-at answers "what sits here", which is what the graph
        // draws. Full refnames so the "<remote>/HEAD" alias is easy to drop; then shorten
        // refs/heads/x, refs/remotes/x and refs/tags/x down to x.
        var branchesResult = await RunAsync(
            repoPath,
            ["for-each-ref", "--points-at", sha, "--format=%(refname)"],
            null,
            cancellationToken).ConfigureAwait(false);

        // Keep the KIND, not just the name: "main" and "origin/main" are otherwise indistinguishable
        // once the prefix is stripped, and the card colours them like the commit-row badges do.
        var branches = new List<GitRef>();
        if (branchesResult.Succeeded)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in branchesResult.StandardOutput.Split('\n'))
            {
                var refName = raw.Trim();
                if (refName.Length == 0 || refName.EndsWith("/HEAD", StringComparison.Ordinal))
                {
                    continue;
                }

                var (name, kind) = refName switch
                {
                    _ when refName.StartsWith("refs/heads/", StringComparison.Ordinal)
                        => (refName["refs/heads/".Length..], GitRefKind.LocalBranch),
                    _ when refName.StartsWith("refs/remotes/", StringComparison.Ordinal)
                        => (refName["refs/remotes/".Length..], GitRefKind.RemoteBranch),
                    _ when refName.StartsWith("refs/tags/", StringComparison.Ordinal)
                        => (refName["refs/tags/".Length..], GitRefKind.Tag),
                    _ => (refName, GitRefKind.LocalBranch),
                };

                if (name.Length > 0 && seen.Add(name))
                {
                    branches.Add(new GitRef(name, kind));
                }
            }
        }

        // Reachable from HEAD? --is-ancestor exits 0 when yes (a commit is its own ancestor), 1 when no.
        var ancestor = await RunAsync(
            repoPath, ["merge-base", "--is-ancestor", sha, "HEAD"], null, cancellationToken).ConfigureAwait(false);

        // "Which branch is this commit on?" — its lineage, when no ref points exactly at it. name-rev
        // names it relative to the nearest branch, e.g. "main~30" (30 back on main's own line) or
        // "main~1^2~4" (into a merge's SECOND parent). Two cases:
        //  • No '^' → the commit is on that branch's own first-parent line. Show "On <branch>".
        //  • A '^' → it was merged in from another branch (often one since deleted), so "on <branch>"
        //    would be wrong and collapses many commits onto the same survivor. The name up to the first
        //    '^' is the MERGE COMMIT that brought it in; its message still records the branch name as it
        //    was at merge time ("Merge pull request #N from owner/x", "Merge branch 'x'"). Show that as
        //    "Merged from <branch>" — the real origin branch, even though the ref is gone.
        // name-rev already prefers the closest ref; tags are excluded (shown above as their own chips).
        var nearestBranch = string.Empty;
        var nearestIsMerge = false;
        if (branches.Count == 0)
        {
            var nameRev = await RunAsync(
                repoPath,
                ["name-rev", "--name-only", "--exclude=refs/tags/*", sha],
                null,
                cancellationToken).ConfigureAwait(false);
            if (nameRev.Succeeded)
            {
                var name = nameRev.StandardOutput.Trim();
                if (name.Length > 0 && name != "undefined")
                {
                    var caret = name.IndexOf('^');
                    if (caret < 0)
                    {
                        var tilde = name.IndexOf('~');   // drop the "~N" distance suffix
                        nearestBranch = tilde >= 0 ? name[..tilde] : name;
                        if (nearestBranch.StartsWith("remotes/", StringComparison.Ordinal))
                        {
                            nearestBranch = nearestBranch["remotes/".Length..];
                        }
                    }
                    else
                    {
                        var mergeRef = name[..caret];   // the merge commit on the named branch's line
                        var mergeMsg = await RunAsync(
                            repoPath, ["log", "-1", "--format=%s", mergeRef], null, cancellationToken).ConfigureAwait(false);
                        if (mergeMsg.Succeeded)
                        {
                            var merged = ParseMergedBranch(mergeMsg.StandardOutput.Trim());
                            if (merged.Length > 0)
                            {
                                nearestBranch = merged;
                                nearestIsMerge = true;
                            }
                        }
                    }
                }
            }
        }

        return new CommitContainment(ancestor.Succeeded, branches, nearestBranch, nearestIsMerge);
    }

    /// <summary>
    /// Pulls the merged branch name out of a merge commit's subject: GitHub's
    /// "Merge pull request #N from owner/branch" (the owner segment is dropped), or git's own
    /// "Merge branch 'branch'" / "Merge remote-tracking branch 'origin/branch'". Empty if it doesn't
    /// look like a recognised merge subject.
    /// </summary>
    internal static string ParseMergedBranch(string subject)
    {
        const string prFrom = " from ";
        if (subject.StartsWith("Merge pull request #", StringComparison.Ordinal))
        {
            var from = subject.IndexOf(prFrom, StringComparison.Ordinal);
            if (from >= 0)
            {
                var spec = subject[(from + prFrom.Length)..].Trim();   // "owner/branch" (branch may have '/')
                var slash = spec.IndexOf('/');
                return slash >= 0 && slash < spec.Length - 1 ? spec[(slash + 1)..] : string.Empty;
            }
        }

        // "Merge branch 'x'", "Merge branch 'x' into y", "Merge remote-tracking branch 'origin/x'".
        if (subject.StartsWith("Merge ", StringComparison.Ordinal))
        {
            var open = subject.IndexOf('\'');
            if (open >= 0)
            {
                var close = subject.IndexOf('\'', open + 1);
                if (close > open + 1)
                {
                    return subject[(open + 1)..close];
                }
            }
        }

        return string.Empty;
    }

    // "M\tpath" lines (one per changed file, --no-renames so a single tab), deduped.
    private static List<CommitFileEntry> ParseNameStatus(string output)
    {
        var files = new List<CommitFileEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in output.Split('\n'))
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

    public async Task<IReadOnlyList<CommitInfo>> GetCommitsBetweenAsync(string repoPath, string baseRef, string compareRef, int maxCount = 300, CancellationToken cancellationToken = default)
    {
        // Commits reachable from compareRef but not baseRef ("what compare has that base doesn't").
        var result = await RunAsync(
            repoPath,
            [
                "log", "--no-show-signature", "--date-order", "--decorate=full",
                "--format=" + CommitLogParser.Format,
                "--max-count=" + maxCount.ToString(CultureInfo.InvariantCulture),
                $"{baseRef}..{compareRef}",
            ],
            null,
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new GitException($"git log {baseRef}..{compareRef} failed: {result.FailureMessage}");
        }

        return CommitLogParser.Parse(result.StandardOutput);
    }

    public async Task<IReadOnlyList<CommitFileEntry>> GetDiffFilesAsync(string repoPath, string baseRef, string compareRef, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            repoPath,
            ["diff", "--name-status", "--no-renames", baseRef, compareRef],
            null,
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new GitException($"git diff --name-status failed: {result.FailureMessage}");
        }

        return ParseNameStatus(result.StandardOutput);
    }

    public async Task<string> GetRefRangeFileDiffAsync(string repoPath, string baseRef, string compareRef, string path, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            repoPath,
            ["diff", baseRef, compareRef, "--", path],
            null,
            cancellationToken).ConfigureAwait(false);

        return result.Succeeded ? result.StandardOutput : result.FailureMessage;
    }

    /// <summary>
    /// Files a merge resolved by hand — where the result matches NEITHER parent. Everything else in a
    /// merge already exists as a commit on one side; this is the content that lives only in the merge
    /// itself, which a plain <c>git log -p</c> never shows.
    /// <para>
    /// The list is parsed out of the combined patch rather than taken from <c>--name-only</c>: that
    /// also reports files whose combined patch turns out empty (identical to one parent), which would
    /// give a file list with nothing to show behind it.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<CommitFileEntry>> GetMergeResolutionFilesAsync(
        string repoPath, string sha, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            repoPath, ["diff-tree", "--cc", "-p", "--no-commit-id", sha], null, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new GitException($"git diff-tree --cc failed: {result.FailureMessage}");
        }

        const string header = "diff --cc ";
        var files = new List<CommitFileEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in result.StandardOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith(header, StringComparison.Ordinal))
            {
                continue;
            }

            var path = line[header.Length..].Trim();
            if (path.Length > 0 && seen.Add(path))
            {
                // A resolution is always a modification of the merged result; there's no add/delete
                // distinction to report here.
                files.Add(new CommitFileEntry(path, "M"));
            }
        }

        return files;
    }

    /// <summary>The combined ("--cc") patch for one file of a merge — the hand-resolved hunks.</summary>
    public async Task<string> GetMergeResolutionFileDiffAsync(
        string repoPath, string sha, string path, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            repoPath,
            ["diff-tree", "--cc", "-p", "--no-commit-id", sha, "--", path],
            null,
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new GitException($"git diff-tree --cc (file) failed: {result.FailureMessage}");
        }

        return result.StandardOutput;
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

    public Task<GitCommandResult> CreateBranchAsync(
        string repoPath,
        string name,
        bool checkout = true,
        string? startPoint = null,
        CancellationToken cancellationToken = default)
    {
        // No start point means "branch from HEAD", which is git's own default — so leave the argument
        // off entirely rather than passing the current branch explicitly.
        var start = string.IsNullOrWhiteSpace(startPoint) ? null : startPoint.Trim();

        List<string> args = checkout ? ["checkout", "-b", name] : ["branch", name];
        if (start is not null)
        {
            args.Add(start);
        }

        return RunAsync(repoPath, [.. args], null, cancellationToken);
    }

    public Task<GitCommandResult> DeleteBranchAsync(string repoPath, string name, bool force = false, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["branch", force ? "-D" : "-d", name], null, cancellationToken);

    public Task<GitCommandResult> MergeAsync(string repoPath, string branch, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["merge", branch], null, cancellationToken);

    public Task<GitCommandResult> RenameBranchAsync(string repoPath, string oldName, string newName, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["branch", "-m", oldName, newName], null, cancellationToken);

    public Task<GitCommandResult> SetUpstreamAsync(string repoPath, string branch, string upstream, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["branch", "--set-upstream-to=" + upstream, branch], null, cancellationToken);

    public Task<GitCommandResult> UnsetUpstreamAsync(string repoPath, string branch, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["branch", "--unset-upstream", branch], null, cancellationToken);

    public async Task<IReadOnlyList<string>> GetRemoteBranchesAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(repoPath, ["for-each-ref", "--format=%(refname:short)", "refs/remotes"], null, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return [];
        }

        var names = new List<string>();
        foreach (var raw in result.StandardOutput.Split('\n'))
        {
            var name = raw.Trim();
            if (name.Length > 0 && !name.EndsWith("/HEAD", StringComparison.Ordinal))
            {
                names.Add(name);
            }
        }

        return names;
    }

    public Task<GitCommandResult> CherryPickAsync(string repoPath, string sha, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["cherry-pick", sha], null, cancellationToken);

    public Task<GitCommandResult> CreateTagAsync(string repoPath, string name, string sha, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["tag", name, sha], null, cancellationToken);

    public async Task<IReadOnlyList<GitTag>> GetTagsAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        // Newest first, like most clients show them.
        var result = await RunAsync(repoPath, ["tag", "--sort=-creatordate"], null, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new GitException($"git tag failed: {result.FailureMessage}");
        }

        var tags = new List<GitTag>();
        foreach (var raw in result.StandardOutput.Split('\n'))
        {
            var name = raw.Trim();
            if (name.Length > 0)
            {
                tags.Add(new GitTag(name));
            }
        }

        return tags;
    }

    public Task<GitCommandResult> DeleteTagsAsync(string repoPath, IReadOnlyList<string> names, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "tag", "-d" };
        args.AddRange(names);
        return RunAsync(repoPath, args, null, cancellationToken);
    }

    public Task<GitCommandResult> DeleteRemoteTagsAsync(string repoPath, string remote, IReadOnlyList<string> names, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "push", remote, "--delete" };
        foreach (var name in names)
        {
            args.Add($"refs/tags/{name}");
        }
        return RunAsync(repoPath, args, null, cancellationToken);
    }

    public Task<GitCommandResult> PushTagsAsync(string repoPath, string remote, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["push", remote, "--tags", "--progress"], progress, cancellationToken);

    public async Task<IReadOnlyCollection<string>> GetRemoteTagNamesAsync(string repoPath, string remote, CancellationToken cancellationToken = default)
    {
        // "<sha>\trefs/tags/<name>" per line, with a "^{}" peeled line for annotated tags.
        var result = await RunAsync(repoPath, ["ls-remote", "--tags", remote], null, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new GitException($"git ls-remote failed: {result.FailureMessage}");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in result.StandardOutput.Split('\n'))
        {
            var tab = raw.IndexOf('\t');
            if (tab < 0)
            {
                continue;
            }

            var reference = raw[(tab + 1)..].Trim();
            const string prefix = "refs/tags/";
            if (!reference.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var name = reference[prefix.Length..];
            if (name.EndsWith("^{}", StringComparison.Ordinal))
            {
                name = name[..^3];   // the peeled ref for an annotated tag — same name
            }

            if (name.Length > 0)
            {
                names.Add(name);
            }
        }

        return names;
    }

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

    public Task<GitCommandResult> StashPushAsync(
        string repoPath,
        string? message = null,
        bool includeUntracked = false,
        bool stagedOnly = false,
        CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "stash", "push" };
        if (stagedOnly)
        {
            args.Add("--staged");        // only the index (git 2.35+)
        }
        if (includeUntracked)
        {
            args.Add("--include-untracked");
        }
        if (!string.IsNullOrEmpty(message))
        {
            args.Add("-m");
            args.Add(message);
        }
        return RunAsync(repoPath, args, null, cancellationToken);
    }

    public Task<GitCommandResult> StashPopAsync(string repoPath, int index = 0, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["stash", "pop", $"stash@{{{index}}}"], null, cancellationToken);

    public Task<GitCommandResult> StashApplyAsync(string repoPath, int index = 0, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["stash", "apply", $"stash@{{{index}}}"], null, cancellationToken);

    public Task<GitCommandResult> StashDropAsync(string repoPath, int index, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["stash", "drop", $"stash@{{{index}}}"], null, cancellationToken);

    public Task<GitCommandResult> StashClearAsync(string repoPath, CancellationToken cancellationToken = default)
        => RunAsync(repoPath, ["stash", "clear"], null, cancellationToken);

    public async Task<string> GetStashDiffAsync(string repoPath, int index, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            repoPath,
            ["stash", "show", "-p", "--include-untracked", $"stash@{{{index}}}"],
            null,
            cancellationToken).ConfigureAwait(false);

        return result.Succeeded ? result.StandardOutput : result.FailureMessage;
    }

    public async Task<IReadOnlyList<ReflogEntry>> GetReflogAsync(string repoPath, int maxCount = 200, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            repoPath,
            ["reflog", "--no-show-signature", "--format=" + ReflogParser.Format, "--max-count=" + maxCount.ToString(CultureInfo.InvariantCulture)],
            null,
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return [];
        }

        return ReflogParser.Parse(result.StandardOutput);
    }

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
        var output = string.Empty;

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

                // The two stream reads were started but not awaited on this path; observe them so an
                // abandoned faulted read can't resurface later as an unobserved task exception.
                await ObserveAsync(stdoutTask).ConfigureAwait(false);
                await ObserveAsync(stderrTask).ConfigureAwait(false);
                throw;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            var result = new GitCommandResult(process.ExitCode, stdout, stderr);
            succeeded = result.Succeeded;
            output = BuildLogOutput(stdout, stderr);
            return result;
        }
        finally
        {
            stopwatch.Stop();
            CommandLog.Record(new GitCommandLogEntry(command, succeeded, stopwatch.ElapsedMilliseconds, DateTime.Now, output));
        }
    }

    private const int MaxLoggedOutput = 8000;

    /// <summary>
    /// What to keep of git's output for the command log: both streams, minus the transient progress
    /// redraws (fetch/push spray "Counting objects: 50% (7/13)" lines that only clutter a static log).
    /// Capped, keeping the TAIL — a push/pull summary sits at the end, and a failure message is short
    /// enough to survive whole.
    /// </summary>
    internal static string BuildLogOutput(string stdout, string stderr)
    {
        // git redraws progress with a bare CR (no newline), so normalise every CR to a line break —
        // then each redraw is its own line and can be dropped individually.
        var normalised = (stdout + "\n" + stderr).Replace("\r\n", "\n").Replace('\r', '\n');
        var combined = new StringBuilder();

        foreach (var line in normalised.Split('\n'))
        {
            // git progress: "<phase>: NN% (a/b)". The final line of each phase ends "done." and is
            // kept; the intermediate redraws are noise here.
            if (line.Contains("% (", StringComparison.Ordinal) && !line.Contains("done", StringComparison.Ordinal))
            {
                continue;
            }

            combined.Append(line).Append('\n');
        }

        var text = combined.ToString().Trim();
        return text.Length > MaxLoggedOutput
            ? "… (truncated)\n" + text[^MaxLoggedOutput..]
            : text;
    }

    /// <summary>Awaits an abandoned stream-read, swallowing its fault — used to observe the reads that
    /// were left running when a git call was cancelled.</summary>
    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The call was cancelled; a faulted or cancelled read here is expected and irrelevant.
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
