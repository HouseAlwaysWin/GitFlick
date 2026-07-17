using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GitFlick.Models;

namespace GitFlick.Services;

/// <summary>
/// Thin async wrapper over the git CLI. git is the single source of truth (spec §3): every
/// call names its own working directory, decodes UTF-8, and disables quotepath so CJK paths
/// come back literal.
/// </summary>
public interface IGitService
{
    /// <summary>The running log of git commands this service has executed.</summary>
    GitCommandLog CommandLog { get; }

    /// <summary>The <c>git --version</c> string, or null if git could not be started.</summary>
    Task<string?> GetVersionAsync(CancellationToken cancellationToken = default);

    Task<bool> IsRepositoryAsync(string path, CancellationToken cancellationToken = default);

    Task<GitStatus> GetStatusAsync(string repoPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// The unified diff for one path. <paramref name="staged"/> selects <c>--cached</c>.
    /// An untracked file has nothing to diff against, so it is compared to an empty file
    /// and comes back as all-added.
    /// </summary>
    Task<string> GetDiffAsync(
        string repoPath,
        string path,
        bool staged,
        bool untracked = false,
        CancellationToken cancellationToken = default);

    Task<GitCommandResult> StageAsync(string repoPath, string path, CancellationToken cancellationToken = default);

    Task<GitCommandResult> StageAllAsync(string repoPath, CancellationToken cancellationToken = default);

    Task<GitCommandResult> UnstageAsync(string repoPath, string path, CancellationToken cancellationToken = default);

    Task<GitCommandResult> UnstageAllAsync(string repoPath, CancellationToken cancellationToken = default);

    /// <summary>The full staged diff (<c>git diff --cached</c>) — fed to the AI message generator.</summary>
    Task<string> GetStagedDiffAsync(string repoPath, CancellationToken cancellationToken = default);

    /// <summary>Commit the staged changes. <paramref name="signOff"/> adds a Signed-off-by trailer.</summary>
    Task<GitCommandResult> CommitAsync(string repoPath, string message, bool signOff = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replace the last commit, folding in the staged changes. A null/blank <paramref name="message"/>
    /// keeps the previous message (<c>--no-edit</c>); otherwise it rewords it.
    /// </summary>
    Task<GitCommandResult> CommitAmendAsync(string repoPath, string? message, bool signOff = false, CancellationToken cancellationToken = default);

    /// <summary>Undo the last commit but keep its changes staged (<c>reset --soft HEAD~1</c>).</summary>
    Task<GitCommandResult> UndoLastCommitAsync(string repoPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discard one path's changes. A tracked file is reverted (<c>checkout -- path</c>); an
    /// <paramref name="untracked"/> file/directory is removed (<c>clean -fd -- path</c>).
    /// </summary>
    Task<GitCommandResult> DiscardPathAsync(string repoPath, string path, bool untracked, CancellationToken cancellationToken = default);

    /// <summary>
    /// Throw away every change to tracked files (<c>reset --hard</c>). When
    /// <paramref name="includeUntracked"/> is set, also delete untracked files (<c>clean -fd</c>).
    /// </summary>
    Task<GitCommandResult> DiscardAllAsync(string repoPath, bool includeUntracked, CancellationToken cancellationToken = default);

    Task<GitCommandResult> FetchAsync(string repoPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Fetch, dropping remote-tracking branches whose upstream was deleted (<c>--prune</c>).</summary>
    Task<GitCommandResult> FetchPruneAsync(string repoPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Fetch from every configured remote, not just the current branch's (<c>--all</c>).</summary>
    Task<GitCommandResult> FetchAllAsync(string repoPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    Task<GitCommandResult> PullAsync(string repoPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Pull, replaying local commits on top of upstream instead of merging (<c>--rebase</c>).</summary>
    Task<GitCommandResult> PullRebaseAsync(string repoPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Pull a specific <paramref name="branch"/> from a specific <paramref name="remote"/>.</summary>
    Task<GitCommandResult> PullFromAsync(string repoPath, string remote, string branch, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    Task<GitCommandResult> PushAsync(string repoPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Push a specific <paramref name="branch"/> to a specific <paramref name="remote"/>.</summary>
    Task<GitCommandResult> PushToAsync(string repoPath, string remote, string branch, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>The names of the configured remotes (<c>git remote</c>), empty if none.</summary>
    Task<IReadOnlyList<string>> GetRemotesAsync(string repoPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// History for the graph. <paramref name="firstParentOnly"/> collapses merges to one row each
    /// (spec §5⑦'s "first-parent" view); otherwise every branch/remote/tag tip is included.
    /// Always date-ordered, so a parent never precedes its child — the graph builder depends on it.
    /// </summary>
    Task<IReadOnlyList<CommitInfo>> GetCommitsAsync(
        string repoPath,
        int maxCount = 300,
        bool firstParentOnly = false,
        CancellationToken cancellationToken = default);

    /// <summary>The full patch introduced by one commit.</summary>
    Task<string> GetCommitDiffAsync(string repoPath, string sha, CancellationToken cancellationToken = default);

    /// <summary>The complete commit message (subject + body), fetched on demand for the "view message" popup.</summary>
    Task<string> GetCommitMessageAsync(string repoPath, string sha, CancellationToken cancellationToken = default);

    /// <summary>The files a commit changed (vs its first parent), so the diff can be split per file.</summary>
    Task<IReadOnlyList<CommitFileEntry>> GetCommitFilesAsync(string repoPath, string sha, CancellationToken cancellationToken = default);

    /// <summary>The patch a commit introduced for a single file.</summary>
    Task<string> GetCommitFileDiffAsync(string repoPath, string sha, string path, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repoPath, CancellationToken cancellationToken = default);

    Task<GitCommandResult> CheckoutAsync(string repoPath, string branch, CancellationToken cancellationToken = default);

    Task<GitCommandResult> CreateBranchAsync(string repoPath, string name, bool checkout = true, CancellationToken cancellationToken = default);

    Task<GitCommandResult> DeleteBranchAsync(string repoPath, string name, bool force = false, CancellationToken cancellationToken = default);

    Task<GitCommandResult> MergeAsync(string repoPath, string branch, CancellationToken cancellationToken = default);

    /// <summary>Replays one commit onto the current branch.</summary>
    Task<GitCommandResult> CherryPickAsync(string repoPath, string sha, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StashEntry>> GetStashesAsync(string repoPath, CancellationToken cancellationToken = default);

    Task<GitCommandResult> StashPushAsync(string repoPath, string? message = null, CancellationToken cancellationToken = default);

    Task<GitCommandResult> StashPopAsync(string repoPath, int index = 0, CancellationToken cancellationToken = default);
}
