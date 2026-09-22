using System.Collections.Concurrent;
using System.Diagnostics;
using NeuroCamera.Engine;
using Xunit;

namespace NeuroCamera.Tests;

public class SerialSessionRunnerTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    [Fact]
    public void Sessions_never_run_at_the_same_time_even_when_started_in_a_burst()
    {
        SerialSessionRunner runner = new();
        int concurrent = 0;
        int maxConcurrent = 0;
        int lastRunId = -1;

        for (int id = 0; id < 100; id++)
        {
            int sessionId = id;
            runner.Start(_ =>
            {
                int now = Interlocked.Increment(ref concurrent);
                InterlockedMax(ref maxConcurrent, now);
                Thread.Sleep(2);
                lastRunId = sessionId;
                Interlocked.Decrement(ref concurrent);
            }, "test-session");
        }

        Assert.True(runner.WaitForIdle(Generous));
        Assert.Equal(1, maxConcurrent);
        Assert.Equal(99, lastRunId);
    }

    [Fact]
    public void Start_and_Stop_return_immediately_while_the_current_session_is_stuck()
    {
        SerialSessionRunner runner = new();
        using ManualResetEventSlim release = new(false);
        using ManualResetEventSlim started = new(false);

        runner.Start(_ =>
        {
            started.Set();
            release.Wait();
        }, "stuck");
        Assert.True(started.Wait(Generous));

        Stopwatch stopwatch = Stopwatch.StartNew();
        runner.Start(_ => { }, "next");
        runner.Stop();
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 1000, $"Start/Stop blocked for {stopwatch.ElapsedMilliseconds} ms.");

        release.Set();
        Assert.True(runner.WaitForIdle(Generous));
    }

    [Fact]
    public void A_session_superseded_before_it_could_run_is_skipped()
    {
        SerialSessionRunner runner = new();
        using ManualResetEventSlim gate = new(false);
        ConcurrentQueue<string> ran = new();

        runner.Start(_ =>
        {
            ran.Enqueue("A");
            gate.Wait();
        }, "A");
        Assert.True(SpinWait.SpinUntil(() => ran.Count == 1, Generous));

        runner.Start(_ => ran.Enqueue("B"), "B");
        runner.Start(_ => ran.Enqueue("C"), "C");
        gate.Set();

        Assert.True(runner.WaitForIdle(Generous));
        Assert.Equal(new[] { "A", "C" }, ran.ToArray());
    }

    [Fact]
    public void Stop_cancels_the_token_of_the_running_session_and_clears_IsActive()
    {
        SerialSessionRunner runner = new();
        using ManualResetEventSlim started = new(false);
        CancellationToken captured = default;

        runner.Start(token =>
        {
            captured = token;
            started.Set();
            token.WaitHandle.WaitOne();
        }, "cancellable");
        Assert.True(started.Wait(Generous));
        Assert.True(runner.IsActive);

        runner.Stop();

        Assert.False(runner.IsActive);
        Assert.True(runner.WaitForIdle(Generous));
        Assert.True(captured.IsCancellationRequested);
    }

    [Fact]
    public void Starting_a_new_session_cancels_the_token_of_the_previous_one()
    {
        SerialSessionRunner runner = new();
        using ManualResetEventSlim started = new(false);
        using ManualResetEventSlim secondRan = new(false);
        CancellationToken first = default;

        runner.Start(token =>
        {
            first = token;
            started.Set();
            token.WaitHandle.WaitOne();
        }, "first");
        Assert.True(started.Wait(Generous));

        runner.Start(_ => secondRan.Set(), "second");

        Assert.True(secondRan.Wait(Generous));
        Assert.True(first.IsCancellationRequested);
    }

    [Fact]
    public void IsActive_turns_false_when_the_session_body_finishes_on_its_own()
    {
        SerialSessionRunner runner = new();

        runner.Start(_ => { }, "short");

        Assert.True(SpinWait.SpinUntil(() => !runner.IsActive, Generous));
    }

    [Fact]
    public void A_faulting_session_is_reported_and_does_not_break_later_sessions()
    {
        SerialSessionRunner runner = new();
        using ManualResetEventSlim faulted = new(false);
        using ManualResetEventSlim goodRan = new(false);
        Exception? reported = null;
        runner.SessionFaulted += (_, exception) =>
        {
            reported = exception;
            faulted.Set();
        };

        runner.Start(_ => throw new InvalidOperationException("boom"), "bad");
        Assert.True(faulted.Wait(Generous));

        runner.Start(_ => goodRan.Set(), "good");

        Assert.True(goodRan.Wait(Generous));
        Assert.NotNull(reported);
        Assert.Equal("boom", reported!.Message);
    }

    [Fact]
    public void A_throwing_fault_handler_does_not_take_the_process_down()
    {
        SerialSessionRunner runner = new();
        using ManualResetEventSlim goodRan = new(false);
        runner.SessionFaulted += (_, _) => throw new InvalidOperationException("handler bug");

        runner.Start(_ => throw new InvalidOperationException("boom"), "bad");
        Assert.True(runner.WaitForIdle(Generous));

        runner.Start(_ => goodRan.Set(), "good");
        Assert.True(goodRan.Wait(Generous));
    }

    [Fact]
    public void WaitForIdle_times_out_while_a_session_is_stuck_and_succeeds_once_it_ends()
    {
        SerialSessionRunner runner = new();
        using ManualResetEventSlim release = new(false);
        using ManualResetEventSlim started = new(false);

        runner.Start(_ =>
        {
            started.Set();
            release.Wait();
        }, "stuck");
        Assert.True(started.Wait(Generous));

        Assert.False(runner.WaitForIdle(TimeSpan.FromMilliseconds(100)));

        release.Set();
        Assert.True(runner.WaitForIdle(Generous));
    }

    [Fact]
    public void WaitForIdle_is_immediately_true_when_nothing_was_ever_started()
    {
        Assert.True(new SerialSessionRunner().WaitForIdle(TimeSpan.Zero));
    }

    [Fact]
    public void A_camera_that_ignores_cancellation_while_opening_is_never_opened_twice()
    {
        // Models VideoCapture: opening takes a long time and cannot be interrupted. With the old
        // "join for 3 seconds, then give up" logic the next session opened the device while the
        // previous open was still in progress.
        SerialSessionRunner runner = new();
        int openOrHeld = 0;
        int maxOpenOrHeld = 0;

        void CameraSession(CancellationToken token)
        {
            int now = Interlocked.Increment(ref openOrHeld);
            InterlockedMax(ref maxOpenOrHeld, now);

            Thread.Sleep(300); // opening the device; cannot be cancelled
            while (!token.IsCancellationRequested)
            {
                Thread.Sleep(5);
            }

            Interlocked.Decrement(ref openOrHeld);
        }

        for (int i = 0; i < 5; i++)
        {
            runner.Start(CameraSession, "camera");
            Thread.Sleep(20);
        }

        runner.Stop();
        Assert.True(runner.WaitForIdle(Generous));
        Assert.Equal(1, maxOpenOrHeld);
        Assert.Equal(0, Volatile.Read(ref openOrHeld));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}
