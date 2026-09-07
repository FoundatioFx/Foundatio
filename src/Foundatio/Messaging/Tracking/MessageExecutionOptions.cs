using System;

namespace Foundatio.Messaging;

/// <summary>Optional execution tracking for broker-delivered work. This never schedules a second job-store worker.</summary>
public sealed record MessageExecutionOptions
{
    /// <summary>Logical queue represented by this execution.</summary>
    public required string QueueName { get; init; }
    /// <summary>Application message type used for diagnostics.</summary>
    public Type? MessageType { get; init; }
    /// <summary>Total delivery budget, including the first attempt; negative means unlimited.</summary>
    public int MaxAttempts { get; init; } = 3;
    /// <summary>Delay for a failed attempt, using its one-based attempt number.</summary>
    public Func<int, TimeSpan> RetryBackoff { get; init; } = RetryPolicy.DefaultBackoff;
    /// <summary>Delivery duration used by explicit progress heartbeats.</summary>
    public TimeSpan VisibilityTimeout { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>Settle returned outcomes automatically; explicit settlement takes precedence.</summary>
    public bool AutoComplete { get; init; } = true;
    /// <summary>Read and update broker execution history in the supplied store.</summary>
    public bool TrackProgress { get; init; }
    /// <summary>Header containing the producer-created execution identifier.</summary>
    public string ExecutionIdHeader { get; init; } = "message.execution.id";
    /// <summary>Retention refreshed by execution state updates.</summary>
    public TimeSpan StateRetention { get; init; } = TimeSpan.FromHours(24);
    /// <summary>Interval between cooperative cancellation checks and execution heartbeats.</summary>
    public TimeSpan CancellationPollInterval { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Process identity recorded when an attempt starts.</summary>
    public string WorkerId { get; init; } = $"{Environment.MachineName}:{Environment.ProcessId}";
    /// <summary>Receives success, failed-attempt, and dead-letter measurements. Must not throw.</summary>
    public Action<MessageOutcomeKind, TimeSpan>? OnProcessed { get; init; }
}
