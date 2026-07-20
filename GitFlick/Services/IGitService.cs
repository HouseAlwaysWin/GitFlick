using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GitFlick.Models;

namespace GitFlick.Services;

/// <summary>How <c>git reset</c> treats the index and working tree when moving the branch.</summary>
public enum GitResetMode
{
    /// <summary>Move the branch only; keep the index and working tree (changes become staged).</summary>
    Soft,

    /// <summary>Move the branch and reset the index; keep the working tree (git's default).</summary>
    Mixed,

    /// <summary>Move the branch and discard all index and working-tree changes. Destructive.</summary>
    Hard,
}

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

    /// <summary>A remote's fetch URL (<c>git remote get-url</c>), for building web links. Null if unset.</summary>
    Task<string?> GetRemoteUrlAsync(string repoPath, string remote, CancellationToken cancellationToken = default);

    /// <summary>
    /// History for the graph. <paramref name="firstParentOnly"/> collapses merges to one row each
    /// (spec §5⑦'s "first-parent" view); otherwise every branch/remote/tag tip is included.
    /// <paramref name="mergesOnly"/> lists just the merge commits. Always date-ordered, so a parent
    /// never precedes its child — the graph builder depends on it.
    /// </summary>
    Task<IReadOnlyList<CommitInfo>> GetCommitsAsync(
        string repoPath,
        int maxCount = 300,
        bool firstParentOnly = false,
        string? pathFilter = null,
        string? contentSearch = null,
        bool mergesOnly = false,
        CancellationToken cancellationToken = default);

    /// <summary>Per-line authorship of a file (<c>git blame --porcelain</c>), optionally as of <paramref name="rev"/>.</summary>
    Task<IReadOnlyList<BlameLine>> GetBlameAsync(string repoPath, string path, string? rev = null, CancellationToken cancellationToken = default);

    /// <summary>Every path that ever appeared in history (incl. renamed/deleted). For file-filter autocomplete.</summary>
    Task<IReadOnlyList<string>> GetAllPathsAsync(string repoPath, CancellationToken cancellationToken = default);

    /// <summary>The full patch introduced by one commit.</summary>
    Task<string> GetCommitDiffAsync(string repoPath, string sha, CancellationToken cancellationToken = default);

    /// <summary>The complete commit message (subject + body), fetched on demand for the "view message" popup.</summary>
    Task<string> GetCommitMessageAsync(string repoPath, string sha, CancellationToken cancellationToken = default);

    /// <summary>The files a commit changed (vs its first parent), so the diff can be split per file.</summary>
    Task<IReadOnlyList<CommitFileEntry>> GetCommitFilesAsync(string repoPath, string sha, CancellationToken cancellationToken = default);

    /// <summary>Whether a commit is reachable from HEAD, and which branches contain it (for the commit hover popup).</summary>
    Task<CommitContainment> GetCommitContainmentAsync(string repoPath, string sha, CancellationToken cancellationToken = default);

    /// <summary>Commits in <paramref name="compareRef"/> but not <paramref name="baseRef"/> (<c>log base..compare</c>).</summary>
    Task<IReadOnlyList<CommitInfo>> GetCommitsBetweenAsync(string repoPath, string baseRef, string compareRef, int maxCount = 300, CancellationToken cancellationToken = default);

    /// <summary>Files that differ between two refs (<c>git diff --name-status base compare</c>).</summary>
    Task<IReadOnlyList<CommitFileEntry>> GetDiffFilesAsync(string repoPath, string baseRef, string compareRef, CancellationToken cancellationToken = default);

    /// <summary>The patch for one file across a ref range (<c>git diff base compare -- path</c>).</summary>
    Task<string> GetRefRangeFileDiffAsync(string repoPath, string baseRef, string compareRef, string path, CancellationToken cancellationToken = default);

    /// <summary>The patch a commit introduced for a single file.</summary>
    Task<string> GetCommitFileDiffAsync(string repoPath, string sha, string path, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repoPath, CancellationToken cancellationToken = default);

    Task<GitCommandResult> CheckoutAsync(string repoPath, string branch, CancellationToken cancellationToken = default);

    Task<GitCommandResult> CreateBranchAsync(string repoPath, string name, bool checkout = true, CancellationToken cancellationToken = default);

    Task<GitCommandResult> DeleteBranchAsync(string repoPath, string name, bool force = false, CancellationToken cancellationToken = default);

    Task<GitCommandResult> MergeAsync(string repoPath, string branch, CancellationToken cancellationToken = default);

    /// <summary>Renames a branch (<c>git branch -m</c>).</summary>
    Task<GitCommandResult> RenameBranchAsync(string repoPath, string oldName, string newName, CancellationToken cancellationToken = default);

    /// <summary>Sets a branch's upstream (<c>git branch --set-upstream-to</c>).</summary>
    Task<GitCommandResult> SetUpstreamAsync(string repoPath, string branch, string upstream, CancellationToken cancellationToken = default);

    /// <summary>Clears a branch's upstream (<c>git branch --unset-upstream</c>).</summary>
    Task<GitCommandResult> UnsetUpstreamAsync(string repoPath, string branch, CancellationToken cancellationToken = default);

    /// <summary>Remote-tracking branch names (<c>refs/remotes</c>, minus <c>*/HEAD</c>) — upstream picker source.</summary>
    Task<IReadOnlyList<string>> GetRemoteBranchesAsync(string repoPath, CancellationToken cancellationToken = default);

    /// <summary>Replays one commit onto the current branch.</summary>
    Task<GitCommandResult> CherryPickAsync(string repoPath, string sha, CancellationToken cancellationToken = default);

    /// <summary>Creates a lightweight tag pointing at a commit (pass "HEAD" to tag the current commit).</summary>
    Task<GitCommandResult> CreateTagAsync(string repoPath, string name, string sha, CancellationToken cancellationToken = default);

    /// <summary>The repo's tags, newest first (<c>git tag --sort=-creatordate</c>).</summary>
    Task<IReadOnlyList<GitTag>> GetTagsAsync(string repoPath, CancellationToken cancellationToken = default);

    /// <summary>Deletes one or more local tags (<c>git tag -d</c>).</summary>
    Task<GitCommandResult> DeleteTagsAsync(string repoPath, IReadOnlyList<string> names, CancellationToken cancellationToken = default);

    /// <summary>Deletes tags on a remote (<c>git push &lt;remote&gt; --delete refs/tags/…</c>).</summary>
    Task<GitCommandResult> DeleteRemoteTagsAsync(string repoPath, string remote, IReadOnlyList<string> names, CancellationToken cancellationToken = default);

    /// <summary>Pushes all tags to a remote (<c>git push &lt;remote&gt; --tags</c>).</summary>
    Task<GitCommandResult> PushTagsAsync(string repoPath, string remote, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>The tag names that exist on a remote (<c>git ls-remote --tags</c>), for the "on remote" marker.</summary>
    Task<IReadOnlyCollection<string>> GetRemoteTagNamesAsync(string repoPath, string remote, CancellationToken cancellationToken = default);

    /// <summary>Creates a branch at a specific commit without switching to it.</summary>
    Task<GitCommandResult> CreateBranchAtAsync(string repoPath, string name, string sha, CancellationToken cancellationToken = default);

    /// <summary>Reverts a commit, recording a new commit that undoes it.</summary>
    Task<GitCommandResult> RevertAsync(string repoPath, string sha, CancellationToken cancellationToken = default);

    /// <summary>Rebases the current branch onto a commit.</summary>
    Task<GitCommandResult> RebaseOntoAsync(string repoPath, string sha, CancellationToken cancellationToken = default);

    /// <summary>Moves the current branch to a commit (soft/mixed/hard).</summary>
    Task<GitCommandResult> ResetToAsync(string repoPath, string sha, GitResetMode mode, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StashEntry>> GetStashesAsync(string repoPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stash the working changes. <paramref name="includeUntracked"/> adds untracked files
    /// (<c>--include-untracked</c>); <paramref name="stagedOnly"/> stashes just the index (<c>--staged</c>).
    /// </summary>
    Task<GitCommandResult> StashPushAsync(string repoPath, string? message = null, bool includeUntracked = false, bool stagedOnly = false, CancellationToken cancellationToken = default);

    /// <summary>Restore a stash and remove it from the list (<c>stash pop</c>).</summary>
    Task<GitCommandResult> StashPopAsync(string repoPath, int index = 0, CancellationToken cancellationToken = default);

    /// <summary>Restore a stash but keep it in the list (<c>stash apply</c>).</summary>
    Task<GitCommandResult> StashApplyAsync(string repoPath, int index = 0, CancellationToken cancellationToken = default);

    /// <summary>Delete one stash (<c>stash drop</c>).</summary>
    Task<GitCommandResult> StashDropAsync(string repoPath, int index, CancellationToken cancellationToken = default);

    /// <summary>Delete every stash (<c>stash clear</c>).</summary>
    Task<GitCommandResult> StashClearAsync(string repoPath, CancellationToken cancellationToken = default);

    /// <summary>The patch a stash holds (<c>stash show -p</c>), for the "view stash" diff.</summary>
    Task<string> GetStashDiffAsync(string repoPath, int index, CancellationToken cancellationToken = default);

    /// <summary>Recent HEAD moves (<c>git reflog</c>) for the reflog view / recovery.</summary>
    Task<IReadOnlyList<ReflogEntry>> GetReflogAsync(string repoPath, int maxCount = 200, CancellationToken cancellationToken = default);
}
