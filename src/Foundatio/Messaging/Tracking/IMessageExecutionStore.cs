using Foundatio.Jobs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace Foundatio.Messaging;

/// <summary>
/// Stores the state of tracked queue jobs. Implementations must be safe for concurrent use.
/// </summary>
public interface IMessageExecutionStore
{
    /// <summary>Whether jobs and cancellation requests are shared across processes. Decorators must forward this capability.</summary>
    bool IsShared { get; }

    /// <summary>
    /// Creates or replaces a job's state. Called once at enqueue time with <see cref="MessageExecutionStatus.Queued"/>.
    /// </summary>
    Task SetJobStateAsync(MessageExecutionState state, TimeSpan? expiry = null, CancellationToken cancellationToken = default);

    /// <summary>Gets retained execution state, or null when absent or expired.</summary>
    Task<MessageExecutionState?> GetJobStateAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a job's status and optional fields. Implementations should apply the change atomically
    /// relative to other status updates for the same job. Returns false for a missing job, a terminal
    /// job, an older attempt, or a repeated start of an attempt already waiting for retry.
    /// Starting an attempt resets progress and its message, initializes the heartbeat, and records workerId when supplied.
    /// Administrative replay creates a new job identity; SetJobStateAsync is an explicit administrative replacement.
    /// </summary>
    Task<bool> UpdateJobStatusAsync(string jobId, MessageExecutionStatus status, DateTimeOffset? startedUtc = null, DateTimeOffset? completedUtc = null, string? errorMessage = null, int? progress = null, int? attempt = null, TimeSpan? expiry = null, CancellationToken cancellationToken = default, string? workerId = null);

    /// <summary>Updates progress only for a live processing attempt; stale attempts are ignored.</summary>
    Task UpdateJobProgressAsync(string jobId, int progress, string? progressMessage = null, TimeSpan? expiry = null, CancellationToken cancellationToken = default, int? expectedAttempt = null);

    /// <summary>
    /// Signals that processing is still alive. The execution pipeline polls and heartbeats
    /// independently of the broker lease supervisor. Only the current processing attempt may update it.
    /// </summary>
    Task HeartbeatAsync(string jobId, CancellationToken cancellationToken = default, int? expectedAttempt = null, TimeSpan? expiry = null);

    /// <summary>Requests cooperative cancellation; returns false for missing or terminal executions.</summary>
    Task<bool> RequestCancellationAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Checks whether cooperative cancellation was requested.</summary>
    Task<bool> IsCancellationRequestedAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Removes retained history and cancellation state; does not remove broker work.</summary>
    Task RemoveJobStateAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Adds an operational counter to the current hourly bucket.</summary>
    Task IncrementCounterAsync(string queueName, string counterName, long value = 1, CancellationToken cancellationToken = default);

    /// <summary>Reads hourly operational counters within the requested time window.</summary>
    Task<JobCounterStats> GetCounterStatsAsync(string queueName, TimeSpan? window = null, CancellationToken cancellationToken = default);

    /// <summary>Reads executions by status in descending creation order.</summary>
    Task<IReadOnlyList<MessageExecutionState>> GetJobsByStatusAsync(string queueName, MessageExecutionStatus status, int skip = 0, int take = 50, CancellationToken cancellationToken = default);

    /// <summary>Counts retained executions with the requested status.</summary>
    Task<long> GetJobCountByStatusAsync(string queueName, MessageExecutionStatus status, CancellationToken cancellationToken = default);
}
