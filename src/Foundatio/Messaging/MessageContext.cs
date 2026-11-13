using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging;

internal sealed class MessageContext : IMessageContext
{
    private static readonly AsyncLocal<MessageContext?> CurrentContext = new();

    private int _progress = -1;
    private string? _progressMessage;
    private long _progressEmittedAt;

    public required string MessageId { get; init; }
    public required string MessageType { get; init; }
    public string? CorrelationId { get; init; }
    public int Attempt { get; init; }
    public DateTimeOffset? SentAt { get; init; }
    public string? GroupKey { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
    public required string Source { get; init; }
    public IServiceProvider Services { get; set; } = Testing.EmptyServiceProvider.Instance;

    /// <summary>The handler's span, which progress reports add events to.</summary>
    public Activity? Activity { get; init; }

    /// <summary>Logs progress reports at Debug.</summary>
    public ILogger? Logger { get; init; }

    public string? DeadLetterReason { get; private set; }
    public bool RetryRequested { get; private set; }
    public TimeSpan? RetryDelay { get; private set; }

    /// <summary>The last reported percentage, or null.</summary>
    public int? Progress => Volatile.Read(ref _progress) is >= 0 and var percent ? percent : null;

    /// <summary>The last reported progress message, or null.</summary>
    public string? ProgressMessage => Volatile.Read(ref _progressMessage);

    /// <summary>The message being handled on this async flow, used to propagate the correlation id.</summary>
    public static MessageContext? Current
    {
        get => CurrentContext.Value;
        set => CurrentContext.Value = value;
    }

    public Task ReportProgressAsync(int? percent = null, string? message = null, CancellationToken cancellationToken = default)
    {
        HandlerProgress.ThrowIfInvalid(percent);
        if (percent is { } value)
            Volatile.Write(ref _progress, value);
        if (message is not null)
            Volatile.Write(ref _progressMessage, message);

        if (ShouldEmit(percent))
            Emit();

        return Task.CompletedTask;
    }

    public void DeadLetter(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        DeadLetterReason = reason;
    }

    public void Retry(TimeSpan? delay = null)
    {
        if (delay is { } value)
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero, nameof(delay));

        RetryRequested = true;
        RetryDelay = delay;
    }

    /// <summary>Lets a report through at most every <see cref="HandlerProgress.Interval"/>, and always at 100 percent.</summary>
    private bool ShouldEmit(int? percent)
    {
        long now = Stopwatch.GetTimestamp();
        long last = Volatile.Read(ref _progressEmittedAt);
        if (percent != 100 && last != 0 && Stopwatch.GetElapsedTime(last, now) < HandlerProgress.Interval)
            return false;

        return Interlocked.CompareExchange(ref _progressEmittedAt, now, last) == last || percent == 100;
    }

    private void Emit()
    {
        int? percent = Progress;
        string? message = ProgressMessage;
        Activity?.AddEvent(new ActivityEvent("progress", tags: new ActivityTagsCollection
        {
            ["progress.percent"] = percent,
            ["progress.message"] = message
        }));

        Logger?.LogDebug("Message {MessageId} progress: {Percent}% {ProgressMessage}", MessageId, percent, message);
    }
}
