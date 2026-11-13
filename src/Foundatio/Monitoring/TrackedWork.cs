using System.Threading;

namespace Foundatio.Monitoring;

/// <summary>
/// A message delivery or job run that a <see cref="WorkTracker"/> follows while it runs. The handler's context is the
/// entry itself, linked into the tracker's lists, so following work allocates nothing.
/// </summary>
internal abstract class TrackedWork
{
    private int _progress = -1;
    private string? _progressMessage;
    private long _progressTimestamp;

    // Read and written by the tracker; the links only under the lock of the tracker's stripe.
    internal WorkTracker? Tracker;
    internal WorkCounter? Counter;
    internal string? Handler;
    internal TrackedWork? Previous;
    internal TrackedWork? Next;
    internal int Stripe;
    internal long StartedTimestamp;
    internal long StoppedTimestamp;
    internal bool Running;

    /// <summary>The message or job id.</summary>
    internal abstract string TrackedId { get; }

    /// <summary>The 1-based attempt.</summary>
    internal abstract int TrackedAttempt { get; }

    /// <summary>The correlation id, or null.</summary>
    internal abstract string? TrackedCorrelationId { get; }

    /// <summary>The last reported percentage, or null.</summary>
    public int? Progress => Volatile.Read(ref _progress) is >= 0 and var percent ? percent : null;

    /// <summary>The last reported progress message, or null.</summary>
    public string? ProgressMessage => Volatile.Read(ref _progressMessage);

    /// <summary>When progress was last reported, as a <see cref="System.TimeProvider"/> timestamp, or 0.</summary>
    internal long ProgressTimestamp => Volatile.Read(ref _progressTimestamp);

    /// <summary>Records reported progress; a null argument keeps the previous value. The caller validates it.</summary>
    protected void UpdateProgress(int? percent, string? message)
    {
        if (percent is { } value)
            Volatile.Write(ref _progress, value);
        if (message is not null)
            Volatile.Write(ref _progressMessage, message);

        if (Tracker is { } tracker)
        {
            Volatile.Write(ref _progressTimestamp, tracker.GetTimestamp());
            tracker.OnProgress();
        }
    }
}
