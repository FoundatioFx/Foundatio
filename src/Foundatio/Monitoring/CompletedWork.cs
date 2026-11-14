using System;

namespace Foundatio.Monitoring;

/// <summary>
/// A message delivery or job run that finished recently: one that was listed as active, or one that did not succeed.
/// Each instance keeps the last <see cref="MonitoringOptions.RecentCapacity"/>.
/// </summary>
public sealed record CompletedWork
{
    /// <summary>Whether this was a message or a job.</summary>
    public WorkKind Kind { get; init; }

    /// <summary>The message id or job id.</summary>
    public required string Id { get; init; }

    /// <summary>The message type name or job name.</summary>
    public required string Name { get; init; }

    /// <summary>Where a message was received from, for example <c>queue:orders</c>. Null for jobs.</summary>
    public string? Source { get; init; }

    /// <summary>The handler or job class that ran it, or null when it ended without running, for example a message no handler matched.</summary>
    public string? Handler { get; init; }

    /// <summary>The 1-based attempt.</summary>
    public int Attempt { get; init; }

    /// <summary>The correlation id it was sent or enqueued with, or null.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>When the handler started, by the instance's clock.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When the handler finished, by the instance's clock.</summary>
    public DateTimeOffset CompletedAt { get; init; }

    /// <summary>How long the handler ran. Zero when it ended without running.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>How it ended.</summary>
    public WorkOutcome Outcome { get; init; }

    /// <summary>
    /// Why it did not succeed: the exception's type and message, or the dead-letter reason, cut to 1,024 characters.
    /// Never a stack trace, payload or header.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>The last reported percentage, or null.</summary>
    public int? Progress { get; init; }

    /// <summary>The last reported progress message, or null.</summary>
    public string? ProgressMessage { get; init; }

    /// <summary>The instance that ran it.</summary>
    public required string InstanceId { get; init; }
}
