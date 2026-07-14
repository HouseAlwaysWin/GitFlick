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

    Task<GitCommandResult> CommitAsync(string repoPath, string message, CancellationToken cancellationToken = default);

    Task<GitCommandResult> FetchAsync(string repoPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    Task<GitCommandResult> PullAsync(string repoPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    Task<GitCommandResult> PushAsync(string repoPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repoPath, CancellationToken cancellationToken = default);

    Task<GitCommandResult> CheckoutAsync(string repoPath, string branch, CancellationToken cancellationToken = default);

    Task<GitCommandResult> CreateBranchAsync(string repoPath, string name, bool checkout = true, CancellationToken cancellationToken = default);

    Task<GitCommandResult> DeleteBranchAsync(string repoPath, string name, bool force = false, CancellationToken cancellationToken = default);

    Task<GitCommandResult> MergeAsync(string repoPath, string branch, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StashEntry>> GetStashesAsync(string repoPath, CancellationToken cancellationToken = default);

    Task<GitCommandResult> StashPushAsync(string repoPath, string? message = null, CancellationToken cancellationToken = default);

    Task<GitCommandResult> StashPopAsync(string repoPath, int index = 0, CancellationToken cancellationToken = default);
}
