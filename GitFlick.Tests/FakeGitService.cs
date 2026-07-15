using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GitFlick.Models;
using GitFlick.Services;

namespace GitFlick.Tests;

/// <summary>
/// A no-op git service for tests that exercise palette/navigation logic. Keeps them from
/// spawning real git subprocesses (which would race the temp-folder cleanup).
/// </summary>
internal sealed class FakeGitService : IGitService
{
    private static readonly GitCommandResult Ok = new(0, string.Empty, string.Empty);

    public Task<string?> GetVersionAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<string?>("git version 0.0-fake");

    public Task<bool> IsRepositoryAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    public Task<GitStatus> GetStatusAsync(string repoPath, CancellationToken cancellationToken = default)
        => Task.FromResult(new GitStatus());

    public Task<string> GetDiffAsync(string repoPath, string path, bool staged, bool untracked = false, CancellationToken cancellationToken = default)
        => Task.FromResult(string.Empty);

    public Task<GitCommandResult> StageAsync(string repoPath, string path, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<GitCommandResult> StageAllAsync(string repoPath, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<GitCommandResult> UnstageAsync(string repoPath, string path, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<GitCommandResult> UnstageAllAsync(string repoPath, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<GitCommandResult> CommitAsync(string repoPath, string message, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<GitCommandResult> FetchAsync(string repoPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<GitCommandResult> PullAsync(string repoPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<GitCommandResult> PushAsync(string repoPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<IReadOnlyList<CommitInfo>> GetCommitsAsync(string repoPath, int maxCount = 300, bool firstParentOnly = false, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CommitInfo>>([]);

    public Task<string> GetCommitDiffAsync(string repoPath, string sha, CancellationToken cancellationToken = default)
        => Task.FromResult(string.Empty);

    public Task<IReadOnlyList<CommitFileEntry>> GetCommitFilesAsync(string repoPath, string sha, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CommitFileEntry>>([]);

    public Task<string> GetCommitFileDiffAsync(string repoPath, string sha, string path, CancellationToken cancellationToken = default)
        => Task.FromResult(string.Empty);

    public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repoPath, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<GitBranch>>([]);

    public Task<GitCommandResult> CheckoutAsync(string repoPath, string branch, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<GitCommandResult> CreateBranchAsync(string repoPath, string name, bool checkout = true, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<GitCommandResult> DeleteBranchAsync(string repoPath, string name, bool force = false, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<GitCommandResult> MergeAsync(string repoPath, string branch, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<GitCommandResult> CherryPickAsync(string repoPath, string sha, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<IReadOnlyList<StashEntry>> GetStashesAsync(string repoPath, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<StashEntry>>([]);

    public Task<GitCommandResult> StashPushAsync(string repoPath, string? message = null, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<GitCommandResult> StashPopAsync(string repoPath, int index = 0, CancellationToken cancellationToken = default) => Task.FromResult(Ok);
}
