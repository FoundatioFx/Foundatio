using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace Foundatio.Messaging;

/// <summary>
/// Tracked state of a queued job. Immutable; use <c>with</c> expressions to derive updates.
/// </summary>
public sealed record MessageExecutionState
{
    public required string JobId { get; init; }
    public required string QueueName { get; init; }
    public string MessageType { get; init; } = string.Empty;
    public MessageExecutionStatus Status { get; init; } = MessageExecutionStatus.Queued;
    public int Progress { get; init; }
    public string? ProgressMessage { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
    public int Attempt { get; init; }
    /// <summary>Identity of the worker process that started the current or most recent attempt.</summary>
    public string? WorkerId { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset LastUpdatedUtc { get; init; }

    /// <summary>
    /// When the worker last signalled that the job is alive, through a visibility renewal or a progress
    /// report. A processing job whose heartbeat is stale has most likely lost its worker.
    /// </summary>
    public DateTimeOffset? LastHeartbeatUtc { get; init; }

    /// <summary>
    /// Caller-supplied metadata captured by the producer at enqueue time,
    /// such as a tenant or user id, so stores can index and display jobs by them.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

public enum MessageExecutionStatus
{
    Queued = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4,
    /// <summary>The current attempt did not settle successfully and may be delivered again.</summary>
    RetryPending = 5,
    /// <summary>The transport call failed without confirming whether the message was accepted.</summary>
    EnqueueUnknown = 6
}
