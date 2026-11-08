using System;

namespace Foundatio.Jobs.Store;

/// <summary>How a run ended.</summary>
public sealed record JobCompletion
{
    private JobCompletion()
    {
    }

    /// <summary><see cref="JobStatus.Queued"/> to run again, otherwise a terminal status.</summary>
    public JobStatus Status { get; private init; }

    public string? Message { get; private init; }
    public string? Error { get; private init; }

    /// <summary>For terminal statuses, how long to keep the record before the store may delete it.</summary>
    public TimeSpan RetainFor { get; private init; }

    /// <summary>For <see cref="JobStatus.Queued"/>, how long until the job is available again.</summary>
    public TimeSpan RetryDelay { get; private init; }

    /// <summary>For <see cref="JobStatus.Queued"/>, true when this run should not count as an attempt (it was interrupted by shutdown).</summary>
    public bool RefundAttempt { get; private init; }

    public static JobCompletion Succeeded(TimeSpan retainFor, string? message = null) => new() { Status = JobStatus.Succeeded, RetainFor = retainFor, Message = message };
    public static JobCompletion Failed(TimeSpan retainFor, string error) => new() { Status = JobStatus.Failed, RetainFor = retainFor, Error = error };
    public static JobCompletion Cancelled(TimeSpan retainFor, string? message = null) => new() { Status = JobStatus.Cancelled, RetainFor = retainFor, Message = message };
    public static JobCompletion Retry(TimeSpan delay, string error) => new() { Status = JobStatus.Queued, RetryDelay = delay, Error = error };
    public static JobCompletion Release() => new() { Status = JobStatus.Queued, RefundAttempt = true };
}
