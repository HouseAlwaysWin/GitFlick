using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace GitFlick.Services;

/// <summary>
/// Hands memory back to the OS when the app goes idle. A tray-resident launcher spends most of
/// its life hidden, and by then the git history/diff buffers it allocated are dead weight — a
/// compacting GC plus <c>EmptyWorkingSet</c> drops the working set that Task Manager reports.
///
/// Ported from GimmeCapture's ProcessMemoryTrimService: every request is debounced and cancelled
/// the moment the window is shown again, so a trim never runs while the user is looking.
/// </summary>
public static class ProcessMemoryTrimService
{
    private static readonly IdleMemoryTrimScheduler FullTrimScheduler = new(TrimCore);
    private static readonly IdleMemoryTrimScheduler WorkingSetTrimScheduler = new(TrimWorkingSetCore);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [DllImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    /// <summary>Full trim — compacting GC then working-set release — after the app has been idle.</summary>
    public static Task<bool> RequestIdleTrimAsync(string reason, TimeSpan? delay = null, CancellationToken ct = default) =>
        FullTrimScheduler.RequestTrimAsync(reason, delay ?? TimeSpan.FromSeconds(30), ct);

    /// <summary>Cheap trim — working-set release only, no GC — for a quick reclaim on hide.</summary>
    public static Task<bool> RequestIdleWorkingSetTrimAsync(string reason, TimeSpan? delay = null, CancellationToken ct = default) =>
        WorkingSetTrimScheduler.RequestTrimAsync(reason, delay ?? TimeSpan.FromSeconds(5), ct);

    /// <summary>The window came back: cancel any pending trim so it never runs mid-interaction.</summary>
    public static void NotifyActivity(string reason)
    {
        FullTrimScheduler.NotifyActivity(reason);
        WorkingSetTrimScheduler.NotifyActivity(reason);
    }

    private static void TrimCore(string reason)
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

            ReleaseWorkingSet();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"MemoryTrim '{reason}' failed: {ex.Message}");   // best-effort only
        }
    }

    private static void TrimWorkingSetCore(string reason)
    {
        try
        {
            ReleaseWorkingSet();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"MemoryTrim working-set '{reason}' failed: {ex.Message}");
        }
    }

    private static void ReleaseWorkingSet()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = Process.GetCurrentProcess();
        _ = EmptyWorkingSet(process.Handle);
    }
}

/// <summary>
/// Debounces trim requests: a request waits out a delay, and any activity (or a newer request)
/// cancels it. Requests that land during the wait are folded into one, and back-to-back trims are
/// throttled to <see cref="DefaultMinimumTrimInterval"/>. The clock and delay are injectable so the
/// behaviour is unit-testable without real time.
/// </summary>
internal sealed class IdleMemoryTrimScheduler
{
    private static readonly TimeSpan DefaultMinimumTrimInterval = TimeSpan.FromSeconds(5);

    private readonly object _trimGate = new();
    private readonly Action<string> _trimAction;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _minimumTrimInterval;
    private readonly HashSet<string> _pendingReasons = new(StringComparer.Ordinal);
    private CancellationTokenSource? _pendingTrimCts;
    private long _activityVersion;
    private DateTimeOffset? _lastTrimAtUtc;

    internal IdleMemoryTrimScheduler(
        Action<string> trimAction,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? minimumTrimInterval = null)
    {
        _trimAction = trimAction ?? throw new ArgumentNullException(nameof(trimAction));
        _delayAsync = delayAsync ?? Task.Delay;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _minimumTrimInterval = minimumTrimInterval ?? DefaultMinimumTrimInterval;
    }

    internal bool TrimNow(string reason)
    {
        lock (_trimGate)
        {
            CancelPendingTrimLocked(clearReasons: true);
            _activityVersion++;

            var now = _utcNow();
            if (_lastTrimAtUtc.HasValue && now - _lastTrimAtUtc.Value < _minimumTrimInterval)
            {
                return false;
            }

            _lastTrimAtUtc = now;
            _trimAction(reason);
            return true;
        }
    }

    internal Task<bool> RequestTrimAsync(string reason, TimeSpan delay, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A trim reason is required.", nameof(reason));
        }

        CancellationTokenSource requestCts;
        long observedActivityVersion;
        TimeSpan effectiveDelay;
        lock (_trimGate)
        {
            _pendingReasons.Add(reason.Trim());
            CancelPendingTrimLocked(clearReasons: false);
            requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _pendingTrimCts = requestCts;
            observedActivityVersion = _activityVersion;
            effectiveDelay = CalculateEffectiveDelay(delay, _utcNow());
        }

        return RunPendingTrimAsync(requestCts, observedActivityVersion, effectiveDelay);
    }

    internal void NotifyActivity(string reason)
    {
        lock (_trimGate)
        {
            _activityVersion++;
            CancelPendingTrimLocked(clearReasons: true);
        }
    }

    private async Task<bool> RunPendingTrimAsync(CancellationTokenSource requestCts, long observedActivityVersion, TimeSpan delay)
    {
        try
        {
            await _delayAsync(delay, requestCts.Token).ConfigureAwait(false);

            lock (_trimGate)
            {
                if (!ReferenceEquals(_pendingTrimCts, requestCts)
                    || requestCts.IsCancellationRequested
                    || _activityVersion != observedActivityVersion)
                {
                    return false;
                }

                var now = _utcNow();
                if (_lastTrimAtUtc.HasValue && now - _lastTrimAtUtc.Value < _minimumTrimInterval)
                {
                    return false;
                }

                var combinedReason = string.Join("+", _pendingReasons.OrderBy(static value => value, StringComparer.Ordinal));
                _pendingReasons.Clear();
                _pendingTrimCts = null;
                _lastTrimAtUtc = now;
                _trimAction($"idle:{combinedReason}");
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            lock (_trimGate)
            {
                if (ReferenceEquals(_pendingTrimCts, requestCts))
                {
                    _pendingTrimCts = null;
                    _pendingReasons.Clear();
                }
            }

            requestCts.Dispose();
        }
    }

    private TimeSpan CalculateEffectiveDelay(TimeSpan requestedDelay, DateTimeOffset now)
    {
        var normalizedDelay = requestedDelay < TimeSpan.Zero ? TimeSpan.Zero : requestedDelay;
        if (!_lastTrimAtUtc.HasValue)
        {
            return normalizedDelay;
        }

        var remainingInterval = (_lastTrimAtUtc.Value + _minimumTrimInterval) - now;
        return remainingInterval > normalizedDelay ? remainingInterval : normalizedDelay;
    }

    private void CancelPendingTrimLocked(bool clearReasons)
    {
        _pendingTrimCts?.Cancel();
        _pendingTrimCts = null;
        if (clearReasons)
        {
            _pendingReasons.Clear();
        }
    }
}
