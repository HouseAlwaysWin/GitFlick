using System;
using System.IO;
using System.Threading;

namespace GitFlick.Services;

/// <summary>
/// Watches a repository for work done outside GitFlick — a commit from VS Code, a checkout from the
/// CLI, an editor saving a file — and raises <see cref="Changed"/> once the churn settles.
///
/// git has no change notification, and the background auto-sync only reacts to the <i>remote</i>
/// moving, so anything another tool did locally used to sit invisible until a manual refresh.
/// Watching the working tree and .git together catches all of it: staging, commits, branch switches,
/// and plain file edits.
///
/// Debounced, because one git command touches many paths in a burst (index.lock, the index, refs,
/// then the working tree) — firing per event would mean dozens of reloads for a single commit.
/// </summary>
public sealed class RepositoryWatcher : IDisposable
{
    /// <summary>Quiet period before a burst of filesystem events is reported as one change.</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(600);

    private readonly Timer _debounce;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public RepositoryWatcher()
    {
        // Created stopped: Change() starts it, and each event pushes the deadline back.
        _debounce = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Raised on a background thread once the repository has been quiet for <see cref="Debounce"/>.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Points the watcher at a repository, replacing whatever it was watching. A null or missing path
    /// simply stops it — opening the palette with no repo open shouldn't keep a watcher alive.
    /// </summary>
    public void Watch(string? repositoryPath)
    {
        Stop();

        if (_disposed || string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
        {
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(repositoryPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,

                // A checkout rewrites thousands of paths at once; the default 8KB buffer overflows and
                // drops events (which surfaces as a missed refresh). Take the largest allowed instead.
                InternalBufferSize = 64 * 1024,
            };

            _watcher.Changed += OnFileSystemEvent;
            _watcher.Created += OnFileSystemEvent;
            _watcher.Deleted += OnFileSystemEvent;
            _watcher.Renamed += OnFileSystemEvent;

            // An overflow means "you missed some" — the right answer is still to refresh.
            _watcher.Error += (_, _) => Schedule();

            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A path we can't watch (a network share, a permission wall) just means no live updates —
            // the manual refresh and the periodic auto-sync still work.
            Stop();
        }
    }

    public void Stop()
    {
        _debounce.Change(Timeout.Infinite, Timeout.Infinite);

        if (_watcher is not { } watcher)
        {
            return;
        }

        _watcher = null;
        watcher.EnableRaisingEvents = false;
        watcher.Changed -= OnFileSystemEvent;
        watcher.Created -= OnFileSystemEvent;
        watcher.Deleted -= OnFileSystemEvent;
        watcher.Renamed -= OnFileSystemEvent;
        watcher.Dispose();
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        if (IsNoise(e.FullPath))
        {
            return;
        }

        Schedule();
    }

    private void Schedule() => _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);

    private void Fire()
    {
        if (!_disposed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Churn that says nothing about what the user would see. Object files are written constantly
    /// during a commit or fetch; lock files appear and vanish around every git command; FETCH_HEAD
    /// moves on our own background fetch, which already refreshes itself.
    /// </summary>
    internal static bool IsNoise(string fullPath)
    {
        var path = fullPath.Replace('\\', '/');

        if (path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var git = path.LastIndexOf("/.git/", StringComparison.OrdinalIgnoreCase);
        if (git < 0)
        {
            return false;   // a working-tree path: always meaningful
        }

        var inGit = path[(git + "/.git/".Length)..];
        return inGit.StartsWith("objects/", StringComparison.OrdinalIgnoreCase)
            || inGit.StartsWith("FETCH_HEAD", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _debounce.Dispose();
    }
}
