using System;
using System.Collections.Generic;
using System.Linq;
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

    public GitCommandLog CommandLog { get; } = new();

    public Task<string?> GetVersionAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<string?>("git version 0.0-fake");

    public Task<bool> IsRepositoryAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    /// <summary>The status GetStatusAsync serves; tests set ahead/behind on it.</summary>
    public GitStatus StubStatus { get; set; } = new();

    /// <summary>How many times status was read — a proxy for "did the view refresh".</summary>
    public int StatusCallCount { get; private set; }

    public Task<GitStatus> GetStatusAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        StatusCallCount++;
        return Task.FromResult(StubStatus);
    }

    /// <summary>Optional override for GetDiffAsync, letting tests control its completion timing and content.</summary>
    public Func<string, Task<string>>? DiffOverride { get; set; }

    public Task<string> GetDiffAsync(string repoPath, string path, bool staged, bool untracked = false, CancellationToken cancellationToken = default)
        => DiffOverride is { } hook ? hook(path) : Task.FromResult(string.Empty);

    public Task<GitCommandResult> StageAsync(string repoPath, string path, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<GitCommandResult> StageAllAsync(string repoPath, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<GitCommandResult> UnstageAsync(string repoPath, string path, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<GitCommandResult> UnstageAllAsync(string repoPath, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<string> GetStagedDiffAsync(string repoPath, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);

    /// <summary>Result CommitAsync returns; defaults to success. Lets tests force a commit failure.</summary>
    public GitCommandResult? CommitResult { get; set; }

    public Task<GitCommandResult> CommitAsync(string repoPath, string message, bool signOff = false, CancellationToken cancellationToken = default) => Task.FromResult(CommitResult ?? Ok);

    public Task<GitCommandResult> CommitAmendAsync(string repoPath, string? message, bool signOff = false, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<GitCommandResult> UndoLastCommitAsync(string repoPath, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<GitCommandResult> DiscardPathAsync(string repoPath, string path, bool untracked, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<GitCommandResult> DiscardAllAsync(string repoPath, bool includeUntracked, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    /// <summary>How many times a plain fetch ran — lets tests assert the on-open remote check fired.</summary>
    public int FetchCount { get; private set; }

    public Task<GitCommandResult> FetchAsync(string repoPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        FetchCount++;
        return Task.FromResult(Ok);
    }

    public Task<GitCommandResult> FetchPruneAsync(string repoPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<GitCommandResult> FetchAllAsync(string repoPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<GitCommandResult> PullAsync(string repoPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<GitCommandResult> PullRebaseAsync(string repoPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<GitCommandResult> PullFromAsync(string repoPath, string remote, string branch, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<GitCommandResult> PushAsync(string repoPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<GitCommandResult> PushToAsync(string repoPath, string remote, string branch, IProgress<string>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    /// <summary>Remote/branch the last PublishBranchAsync was given, so tests can assert the publish.</summary>
    public (string Remote, string Branch)? LastPublish { get; private set; }

    public Task<GitCommandResult> PublishBranchAsync(string repoPath, string remote, string branch, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        LastPublish = (remote, branch);
        return Task.FromResult(Ok);
    }

    /// <summary>The identity GetIdentityAsync serves.</summary>
    public GitIdentity StubIdentity { get; set; } = GitIdentity.None;

    /// <summary>Arguments of the last SetIdentityAsync, so tests can assert the scope.</summary>
    public (string Name, string Email, bool Global)? LastIdentitySet { get; private set; }

    public int ClearRepoIdentityCount { get; private set; }

    public Task<GitIdentity> GetIdentityAsync(string repoPath, CancellationToken cancellationToken = default)
        => Task.FromResult(StubIdentity);

    public Task<GitCommandResult> SetIdentityAsync(string repoPath, string name, string email, bool global, CancellationToken cancellationToken = default)
    {
        LastIdentitySet = (name, email, global);
        return Task.FromResult(Ok);
    }

    public Task<GitCommandResult> ClearRepoIdentityAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        ClearRepoIdentityCount++;
        return Task.FromResult(Ok);
    }

    /// <summary>Remotes GetRemotesAsync serves; the remote check bails out when empty.</summary>
    public List<string> StubRemotes { get; } = [];

    public Task<IReadOnlyList<string>> GetRemotesAsync(string repoPath, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(StubRemotes);

    /// <summary>Remotes (name+url) GetRemoteListAsync serves.</summary>
    public List<GitRemote> StubRemoteList { get; } = [];

    /// <summary>The last (name, url) AddRemoteAsync was called with.</summary>
    public (string Name, string Url)? LastRemoteAdded { get; private set; }

    /// <summary>The last name RemoveRemoteAsync was called with.</summary>
    public string? LastRemoteRemoved { get; private set; }

    public Task<IReadOnlyList<GitRemote>> GetRemoteListAsync(string repoPath, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<GitRemote>>(StubRemoteList.ToList());

    public Task<GitCommandResult> AddRemoteAsync(string repoPath, string name, string url, CancellationToken cancellationToken = default)
    {
        LastRemoteAdded = (name, url);
        return Task.FromResult(Ok);
    }

    public Task<GitCommandResult> RemoveRemoteAsync(string repoPath, string name, CancellationToken cancellationToken = default)
    {
        LastRemoteRemoved = name;
        return Task.FromResult(Ok);
    }

    /// <summary>The remote URL GetRemoteUrlAsync serves.</summary>
    public string? StubRemoteUrl { get; set; }

    public Task<string?> GetRemoteUrlAsync(string repoPath, string remote, CancellationToken cancellationToken = default) => Task.FromResult(StubRemoteUrl);

    /// <summary>Newest-first commits the fake serves; GetCommitsAsync honours maxCount like git log.</summary>
    public List<CommitInfo> StubCommits { get; } = [];

    /// <summary>The last pathFilter GetCommitsAsync was called with, so tests can assert on it.</summary>
    public string? LastPathFilter { get; private set; }

    /// <summary>The last contentSearch (pickaxe) GetCommitsAsync was called with.</summary>
    public string? LastContentSearch { get; private set; }

    /// <summary>The last firstParentOnly / mergesOnly flags GetCommitsAsync was called with.</summary>
    public bool LastFirstParentOnly { get; private set; }
    public bool LastMergesOnly { get; private set; }

    /// <summary>The last date bounds and maxCount GetCommitsAsync was called with, so tests can assert them.</summary>
    public System.DateTimeOffset? LastSince { get; private set; }
    public System.DateTimeOffset? LastUntil { get; private set; }
    public int LastMaxCount { get; private set; }

    /// <summary>The last path-exclude / pickaxe modifiers GetCommitsAsync was called with.</summary>
    public string? LastPathExclude { get; private set; }
    public bool LastContentRegex { get; private set; }
    public bool LastContentIgnoreCase { get; private set; }
    public bool LastPathIncludeIgnoreCase { get; private set; }

    /// <summary>How many times the log was read — so a batch can prove it reloaded once, not per filter.</summary>
    public int CommitsCallCount { get; private set; }

    public Task<IReadOnlyList<CommitInfo>> GetCommitsAsync(string repoPath, int maxCount = 300, bool firstParentOnly = false, string? pathFilter = null, string? contentSearch = null, bool mergesOnly = false, System.DateTimeOffset? since = null, System.DateTimeOffset? until = null, string? pathExclude = null, bool contentRegex = false, bool contentIgnoreCase = false, bool pathIncludeIgnoreCase = false, CancellationToken cancellationToken = default)
    {
        CommitsCallCount++;
        LastPathIncludeIgnoreCase = pathIncludeIgnoreCase;
        LastPathFilter = pathFilter;
        LastContentSearch = contentSearch;
        LastFirstParentOnly = firstParentOnly;
        LastMergesOnly = mergesOnly;
        LastSince = since;
        LastUntil = until;
        LastMaxCount = maxCount;
        LastPathExclude = pathExclude;
        LastContentRegex = contentRegex;
        LastContentIgnoreCase = contentIgnoreCase;
        return Task.FromResult<IReadOnlyList<CommitInfo>>(StubCommits.Take(maxCount).ToList());
    }

    /// <summary>Blame lines the fake hands back.</summary>
    public List<BlameLine> StubBlame { get; } = [];

    /// <summary>The last (path, rev) GetBlameAsync was called with.</summary>
    public string? LastBlamePath { get; private set; }
    public string? LastBlameRev { get; private set; }

    public Task<IReadOnlyList<BlameLine>> GetBlameAsync(string repoPath, string path, string? rev = null, CancellationToken cancellationToken = default)
    {
        LastBlamePath = path;
        LastBlameRev = rev;
        return Task.FromResult<IReadOnlyList<BlameLine>>(StubBlame.ToList());
    }

    /// <summary>Paths the fake offers for file-filter autocomplete.</summary>
    public List<string> StubPaths { get; } = [];

    public Task<IReadOnlyList<string>> GetAllPathsAsync(string repoPath, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(StubPaths.ToList());

    public Task<string> GetCommitDiffAsync(string repoPath, string sha, CancellationToken cancellationToken = default)
        => Task.FromResult(string.Empty);

    public Task<string> GetCommitMessageAsync(string repoPath, string sha, CancellationToken cancellationToken = default)
        => Task.FromResult(string.Empty);

    /// <summary>Records the git operations the ViewModel asked for, so tests can assert on them.</summary>
    public List<string> Operations { get; } = [];

    private Task<GitCommandResult> Record(string op)
    {
        Operations.Add(op);
        return Task.FromResult(Ok);
    }

    public Task<GitCommandResult> CreateTagAsync(string repoPath, string name, string sha, CancellationToken cancellationToken = default)
        => Record($"tag {name} {sha}");

    /// <summary>Tags the fake serves.</summary>
    public List<GitTag> StubTags { get; } = [];

    public Task<IReadOnlyList<GitTag>> GetTagsAsync(string repoPath, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<GitTag>>(StubTags.ToList());

    public Task<GitCommandResult> DeleteTagsAsync(string repoPath, IReadOnlyList<string> names, CancellationToken cancellationToken = default)
        => Record($"tag -d {string.Join(' ', names)}");

    public Task<GitCommandResult> DeleteRemoteTagsAsync(string repoPath, string remote, IReadOnlyList<string> names, CancellationToken cancellationToken = default)
        => Record($"push {remote} --delete {string.Join(' ', names)}");

    public Task<GitCommandResult> PushTagsAsync(string repoPath, string remote, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => Record($"push {remote} --tags");

    /// <summary>Tag names the fake reports as existing on the remote.</summary>
    public List<string> StubRemoteTagNames { get; } = [];

    public Task<IReadOnlyCollection<string>> GetRemoteTagNamesAsync(string repoPath, string remote, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyCollection<string>>(StubRemoteTagNames.ToList());

    public Task<GitCommandResult> CreateBranchAtAsync(string repoPath, string name, string sha, CancellationToken cancellationToken = default)
        => Record($"branch {name} {sha}");

    public Task<GitCommandResult> RevertAsync(string repoPath, string sha, CancellationToken cancellationToken = default)
        => Record($"revert {sha}");

    public Task<GitCommandResult> RebaseOntoAsync(string repoPath, string sha, CancellationToken cancellationToken = default)
        => Record($"rebase {sha}");

    public Task<GitCommandResult> ResetToAsync(string repoPath, string sha, GitResetMode mode, CancellationToken cancellationToken = default)
        => Record($"reset {mode} {sha}");

    public Task<IReadOnlyList<CommitFileEntry>> GetCommitFilesAsync(string repoPath, string sha, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CommitFileEntry>>([]);

    public Task<CommitContainment> GetCommitContainmentAsync(string repoPath, string sha, CancellationToken cancellationToken = default)
        => Task.FromResult(CommitContainment.Empty);

    /// <summary>Commits/files the fake serves for compare, and the last refs it was asked about.</summary>
    public List<CommitInfo> StubCompareCommits { get; } = [];
    public List<CommitFileEntry> StubCompareFiles { get; } = [];
    public string? LastCompareBase { get; private set; }
    public string? LastCompareCompare { get; private set; }

    public Task<IReadOnlyList<CommitInfo>> GetCommitsBetweenAsync(string repoPath, string baseRef, string compareRef, int maxCount = 300, CancellationToken cancellationToken = default)
    {
        LastCompareBase = baseRef;
        LastCompareCompare = compareRef;
        return Task.FromResult<IReadOnlyList<CommitInfo>>(StubCompareCommits.ToList());
    }

    public Task<IReadOnlyList<CommitFileEntry>> GetDiffFilesAsync(string repoPath, string baseRef, string compareRef, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CommitFileEntry>>(StubCompareFiles.ToList());

    public Task<string> GetRefRangeFileDiffAsync(string repoPath, string baseRef, string compareRef, string path, CancellationToken cancellationToken = default)
        => Task.FromResult($"diff for {path}");

    public Task<string> GetCommitFileDiffAsync(string repoPath, string sha, string path, CancellationToken cancellationToken = default)
        => Task.FromResult(string.Empty);

    public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repoPath, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<GitBranch>>([]);

    /// <summary>Hand-resolved files GetMergeResolutionFilesAsync serves.</summary>
    public List<CommitFileEntry> StubMergeResolutionFiles { get; } = [];

    public Task<IReadOnlyList<CommitFileEntry>> GetMergeResolutionFilesAsync(string repoPath, string sha, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CommitFileEntry>>(StubMergeResolutionFiles.ToList());

    public Task<string> GetMergeResolutionFileDiffAsync(string repoPath, string sha, string path, CancellationToken cancellationToken = default)
        => Task.FromResult(string.Empty);

    /// <summary>The branch the last CheckoutAsync was given, so tests can assert the DWIM target.</summary>
    public string? LastCheckout { get; private set; }

    public Task<GitCommandResult> CheckoutAsync(string repoPath, string branch, CancellationToken cancellationToken = default)
    {
        LastCheckout = branch;
        return Task.FromResult(Ok);
    }

    /// <summary>The start point the last CreateBranchAsync was given (null = branch from HEAD).</summary>
    public string? LastCreateBranchStartPoint { get; private set; }

    public Task<GitCommandResult> CreateBranchAsync(string repoPath, string name, bool checkout = true, string? startPoint = null, CancellationToken cancellationToken = default)
    {
        LastCreateBranchStartPoint = startPoint;
        return Task.FromResult(Ok);
    }

    public Task<GitCommandResult> DeleteBranchAsync(string repoPath, string name, bool force = false, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<GitCommandResult> MergeAsync(string repoPath, string branch, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    public Task<GitCommandResult> RenameBranchAsync(string repoPath, string oldName, string newName, CancellationToken cancellationToken = default) => Record($"branch -m {oldName} {newName}");

    public Task<GitCommandResult> SetUpstreamAsync(string repoPath, string branch, string upstream, CancellationToken cancellationToken = default) => Record($"branch --set-upstream-to={upstream} {branch}");

    public Task<GitCommandResult> UnsetUpstreamAsync(string repoPath, string branch, CancellationToken cancellationToken = default) => Record($"branch --unset-upstream {branch}");

    /// <summary>Remote branches the fake serves for the upstream picker.</summary>
    public List<string> StubRemoteBranches { get; } = [];

    public Task<IReadOnlyList<string>> GetRemoteBranchesAsync(string repoPath, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(StubRemoteBranches.ToList());

    public Task<GitCommandResult> CherryPickAsync(string repoPath, string sha, CancellationToken cancellationToken = default) => Task.FromResult(Ok);

    /// <summary>Stashes the fake serves.</summary>
    public List<StashEntry> StubStashes { get; } = [];

    public Task<IReadOnlyList<StashEntry>> GetStashesAsync(string repoPath, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<StashEntry>>(StubStashes.ToList());

    public Task<GitCommandResult> StashPushAsync(string repoPath, string? message = null, bool includeUntracked = false, bool stagedOnly = false, CancellationToken cancellationToken = default)
    {
        var flags = (includeUntracked ? " -u" : "") + (stagedOnly ? " --staged" : "");
        return Record($"stash push{flags}");
    }

    public Task<GitCommandResult> StashPopAsync(string repoPath, int index = 0, CancellationToken cancellationToken = default) => Record($"stash pop {index}");

    public Task<GitCommandResult> StashApplyAsync(string repoPath, int index = 0, CancellationToken cancellationToken = default) => Record($"stash apply {index}");

    public Task<GitCommandResult> StashDropAsync(string repoPath, int index, CancellationToken cancellationToken = default) => Record($"stash drop {index}");

    public Task<GitCommandResult> StashClearAsync(string repoPath, CancellationToken cancellationToken = default) => Record("stash clear");

    public Task<string> GetStashDiffAsync(string repoPath, int index, CancellationToken cancellationToken = default)
        => Task.FromResult($"diff for stash {index}");

    /// <summary>Reflog entries the fake serves.</summary>
    public List<ReflogEntry> StubReflog { get; } = [];

    public Task<IReadOnlyList<ReflogEntry>> GetReflogAsync(string repoPath, int maxCount = 200, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ReflogEntry>>(StubReflog.ToList());
}
