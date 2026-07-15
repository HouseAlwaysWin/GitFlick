using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GitFlick.Services;

namespace GitFlick.Tests;

/// <summary>
/// The debounce/cancel logic behind the idle memory trim — ported from GimmeCapture. Time and the
/// delay are injected so nothing here waits on the wall clock.
/// </summary>
public sealed class IdleMemoryTrimSchedulerTests
{
    [Fact]
    public async Task RequestTrim_debounces_and_combines_reasons()
    {
        var delays = new List<TaskCompletionSource>();
        var reasons = new List<string>();
        var scheduler = new IdleMemoryTrimScheduler(
            reasons.Add,
            (_, _) =>
            {
                var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                delays.Add(delay);
                return delay.Task;
            });

        var first = scheduler.RequestTrimAsync("window-hidden", TimeSpan.FromSeconds(2));
        var second = scheduler.RequestTrimAsync("history-closed", TimeSpan.FromSeconds(2));
        delays[0].SetResult();
        delays[1].SetResult();

        Assert.False(await first);   // superseded
        Assert.True(await second);
        Assert.Equal(["idle:history-closed+window-hidden"], reasons);
    }

    [Fact]
    public async Task NotifyActivity_cancels_a_pending_trim()
    {
        var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reasons = new List<string>();
        var scheduler = new IdleMemoryTrimScheduler(reasons.Add, (_, _) => delay.Task);

        var pending = scheduler.RequestTrimAsync("window-hidden", TimeSpan.FromSeconds(2));
        scheduler.NotifyActivity("window-shown");
        delay.SetResult();

        Assert.False(await pending);
        Assert.Empty(reasons);
    }

    [Fact]
    public async Task A_new_request_runs_after_activity_cancels_the_old_one()
    {
        var delays = new List<TaskCompletionSource>();
        var reasons = new List<string>();
        var scheduler = new IdleMemoryTrimScheduler(
            reasons.Add,
            (_, _) =>
            {
                var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                delays.Add(delay);
                return delay.Task;
            });

        var cancelled = scheduler.RequestTrimAsync("window-hidden", TimeSpan.FromSeconds(2));
        scheduler.NotifyActivity("window-shown");
        var replacement = scheduler.RequestTrimAsync("window-hidden-again", TimeSpan.FromSeconds(2));
        delays[0].SetResult();
        delays[1].SetResult();

        Assert.False(await cancelled);
        Assert.True(await replacement);
        Assert.Equal(["idle:window-hidden-again"], reasons);
    }

    [Fact]
    public void TrimNow_throttles_back_to_back_requests()
    {
        var now = DateTimeOffset.UtcNow;
        var reasons = new List<string>();
        var scheduler = new IdleMemoryTrimScheduler(reasons.Add, utcNow: () => now);

        Assert.True(scheduler.TrimNow("first"));
        now += TimeSpan.FromSeconds(1);
        Assert.False(scheduler.TrimNow("second"));   // within the minimum interval
        now += TimeSpan.FromSeconds(5);
        Assert.True(scheduler.TrimNow("third"));

        Assert.Equal(["first", "third"], reasons);
    }
}
