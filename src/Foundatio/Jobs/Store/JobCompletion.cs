using System;

namespace Foundatio.Jobs.Store;

/// <summary>How a run ended.</summary>
public sealed record JobCompletion
{
    private JobCompletion()
    {
    }

    /// <summary><see cref="JobState.Queued"/> to run again, otherwise a terminal status.</summary>
    public JobState State { get; private init; }

    public string? Message { get; private init; }
    public string? Error { get; private init; }

    /// <summary>For terminal statuses, how long to keep the record before the store may delete it.</summary>
    public TimeSpan RetainFor { get; private init; }

    /// <summary>For <see cref="JobState.Queued"/>, how long until the job is available again.</summary>
    public TimeSpan RetryDelay { get; private init; }

    /// <summary>For <see cref="JobState.Queued"/>, true when this run should not count as an attempt (it was interrupted by shutdown).</summary>
    public bool RefundAttempt { get; private init; }

    public static JobCompletion Succeeded(TimeSpan retainFor, string? message = null) => new() { State = JobState.Succeeded, RetainFor = retainFor, Message = message };
    public static JobCompletion Failed(TimeSpan retainFor, string error) => new() { State = JobState.Failed, RetainFor = retainFor, Error = error };
    public static JobCompletion Cancelled(TimeSpan retainFor, string? message = null) => new() { State = JobState.Cancelled, RetainFor = retainFor, Message = message };
    public static JobCompletion Retry(TimeSpan delay, string error) => new() { State = JobState.Queued, RetryDelay = delay, Error = error };
    public static JobCompletion Release() => new() { State = JobState.Queued, RefundAttempt = true };
}
