using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace NeuroCamera.Engine;

/// <summary>
/// A single-slot, latest-value-wins hand-off between a fast producer (the video thread posting
/// up to 30-60 frames per second) and a slower consumer (the UI thread presenting them).
///
/// Without it every frame queues its own UI callback; when the UI thread falls behind (4K
/// frames, a busy window) the queue - and the memory held by the queued frames - grows without
/// bound and the picture lags further and further behind reality. With it, at most one
/// presenter callback is ever pending and it always picks up the newest frame; frames the UI
/// had no time for are simply dropped, which is exactly what a live preview wants.
///
/// Usage: the producer calls <see cref="Post"/> and schedules the consumer only when it
/// returns true; the consumer calls <see cref="TryTake"/>.
/// </summary>
public sealed class LatestValueMailbox<T> where T : class
{
    private T? _value;
    private int _consumerScheduled;

    /// <summary>
    /// Stores <paramref name="value"/>, replacing any value the consumer has not taken yet.
    /// Returns true if the caller must now schedule the consumer (none is pending), false if a
    /// consumer callback is already on its way and will pick this value up.
    /// </summary>
    public bool Post(T value)
    {
        Interlocked.Exchange(ref _value, value);
        return Interlocked.Exchange(ref _consumerScheduled, 1) == 0;
    }

    /// <summary>
    /// Takes the newest value, if any. Must be called by the scheduled consumer: it re-arms
    /// scheduling *before* taking, so a value posted concurrently is never left stranded.
    /// </summary>
    public bool TryTake([NotNullWhen(true)] out T? value)
    {
        Interlocked.Exchange(ref _consumerScheduled, 0);
        value = Interlocked.Exchange(ref _value, null);
        return value is not null;
    }

    /// <summary>Discards the pending value (e.g. when the stream stops, so no stale frame is shown afterwards).</summary>
    public void Clear() => Interlocked.Exchange(ref _value, null);
}
