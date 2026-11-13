using System;

namespace Foundatio.Monitoring;

/// <summary>
/// A message being handled or a job running, listed once it has run for <see cref="MonitoringOptions.ActiveThreshold"/>
/// or reported progress.
/// </summary>
public sealed record ActiveWork
{
    /// <summary>Whether this is a message or a job.</summary>
    public WorkKind Kind { get; init; }

    /// <summary>The message id or job id.</summary>
    public required string Id { get; init; }

    /// <summary>The message type name or job name.</summary>
    public required string Name { get; init; }

    /// <summary>Where a message was received from, for example <c>queue:orders</c>. Null for jobs.</summary>
    public string? Source { get; init; }

    /// <summary>The handler or job class running it. Several handlers sharing a delivery are listed together.</summary>
    public string? Handler { get; init; }

    /// <summary>The 1-based attempt.</summary>
    public int Attempt { get; init; }

    /// <summary>The correlation id it was sent or enqueued with, or null.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>When the handler started, by the instance's clock.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>The last reported percentage, or null.</summary>
    public int? Progress { get; init; }

    /// <summary>The last reported progress message, or null.</summary>
    public string? ProgressMessage { get; init; }

    /// <summary>When progress was last reported, or null.</summary>
    public DateTimeOffset? ProgressAt { get; init; }

    /// <summary>The instance running it.</summary>
    public required string InstanceId { get; init; }
}
