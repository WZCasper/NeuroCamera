using System.Threading;

namespace NeuroCamera.Engine;

/// <summary>
/// Runs "sessions" (for NeuroCamera: one camera capture loop each) on dedicated background
/// threads with two guarantees the capture loop needs:
///
///   1. Two sessions never run at the same time. A new session waits, on its own thread, until
///      the previous one has completely finished - so a camera is never opened while the
///      previous loop still holds it (the situation a "join with a timeout, then give up"
///      approach ends up in whenever opening a camera takes longer than the timeout, which is
///      common at 4K).
///   2. <see cref="Start"/> and <see cref="Stop"/> never block the calling thread, so calling
///      them from the UI thread cannot freeze the window while a camera is opening/closing.
///
/// A session that was superseded before it got a chance to run is skipped entirely: its body
/// is never invoked. Every session receives its own <see cref="CancellationToken"/>, which is
/// cancelled by <see cref="Stop"/> or when a newer session is started.
/// </summary>
public sealed class SerialSessionRunner
{
    private readonly object _gate = new();
    private Session? _current;
    private Session? _last;

    /// <summary>
    /// Raised (on the session's thread) if a session body lets an exception escape. The runner
    /// swallows it after raising this event, because an unhandled exception on a background
    /// thread would otherwise terminate the whole process.
    /// </summary>
    public event EventHandler<Exception>? SessionFaulted;

    /// <summary>True while the most recently started session has neither been stopped/superseded nor finished.</summary>
    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _current is { } session
                    && !session.Cts.IsCancellationRequested
                    && !session.Done.Task.IsCompleted;
            }
        }
    }

    /// <summary>
    /// Cancels the current session (if any) and queues <paramref name="body"/> as the next one.
    /// Returns immediately; the body starts as soon as every earlier session has finished.
    /// </summary>
    public void Start(Action<CancellationToken> body, string threadName, ThreadPriority priority = ThreadPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(body);

        Session session = new();

        lock (_gate)
        {
            _current?.Cts.Cancel();
            session.Predecessor = _last?.Done.Task;
            _current = session;
            _last = session;
        }

        Thread thread = new(() => Run(session, body))
        {
            IsBackground = true,
            Name = threadName,
            Priority = priority
        };
        thread.Start();
    }

    /// <summary>Requests cancellation of the current session. Does not wait for it to finish.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _current?.Cts.Cancel();
            _current = null;
        }
    }

    /// <summary>
    /// Waits until the most recently started session has fully finished (used on shutdown).
    /// Returns true if it finished (or nothing was ever started) within <paramref name="timeout"/>.
    /// </summary>
    public bool WaitForIdle(TimeSpan timeout)
    {
        Session? last;
        lock (_gate)
        {
            last = _last;
        }

        return last is null || last.Done.Task.Wait(timeout);
    }

    private void Run(Session session, Action<CancellationToken> body)
    {
        try
        {
            session.Predecessor?.Wait();
            session.Predecessor = null; // release the chain so finished sessions can be collected

            if (!session.Cts.IsCancellationRequested)
            {
                body(session.Cts.Token);
            }
        }
        catch (Exception ex)
        {
            RaiseFaulted(ex);
        }
        finally
        {
            session.Done.TrySetResult(true);
        }
    }

    private void RaiseFaulted(Exception ex)
    {
        try
        {
            SessionFaulted?.Invoke(this, ex);
        }
        catch
        {
            // A misbehaving handler must not take the process down either.
        }
    }

    // The CancellationTokenSource is intentionally never disposed: it has no timers or linked
    // registrations, and disposing it would race with a concurrent Cancel() from Start/Stop.
    private sealed class Session
    {
        public CancellationTokenSource Cts { get; } = new();

        public TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task? Predecessor { get; set; }
    }
}
