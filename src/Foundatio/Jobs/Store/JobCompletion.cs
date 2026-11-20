using System;
using System.Diagnostics.CodeAnalysis;

namespace Foundatio.Jobs.Store;

/// <summary>How a run ended.</summary>
[Experimental(ProviderApi.DiagnosticId, UrlFormat = ProviderApi.UrlFormat)]
public sealed record JobCompletion
{
    private JobCompletion()
    {
    }

    /// <summary><see cref="JobState.Queued"/> to run again, otherwise a terminal status.</summary>
    public JobState State { get; private init; }

    /// <summary>For terminal statuses, the result message to store as <see cref="JobInfo.Message"/>. Null keeps the last one.</summary>
    public string? Message { get; private init; }
    /// <summary>The error to store as <see cref="JobInfo.Error"/>. Null keeps the last one, except that a success clears it.</summary>
    public string? Error { get; private init; }

    /// <summary>For terminal statuses, how long to keep the record before the store may delete it.</summary>
    public TimeSpan RetainFor { get; private init; }

    /// <summary>For <see cref="JobState.Queued"/>, how long until the job is available again.</summary>
    public TimeSpan RetryDelay { get; private init; }

    /// <summary>For <see cref="JobState.Queued"/>, true when this run should not count as an attempt (it was interrupted by shutdown).</summary>
    public bool RefundAttempt { get; private init; }

    /// <summary>The run succeeded. The store drops the job's arguments and keeps the rest of its record for <paramref name="retainFor"/>.</summary>
    public static JobCompletion Succeeded(TimeSpan retainFor, string? message = null) => new() { State = JobState.Succeeded, RetainFor = retainFor, Message = message };
    /// <summary>The job failed for good, for example with no attempts left. The record is kept for <paramref name="retainFor"/>.</summary>
    public static JobCompletion Failed(TimeSpan retainFor, string error) => new() { State = JobState.Failed, RetainFor = retainFor, Error = error };
    /// <summary>The run stopped because cancellation was requested. The record is kept for <paramref name="retainFor"/>.</summary>
    public static JobCompletion Cancelled(TimeSpan retainFor, string? message = null) => new() { State = JobState.Cancelled, RetainFor = retainFor, Message = message };
    /// <summary>The run failed and the job runs again after <paramref name="delay"/>; the attempt counts.</summary>
    public static JobCompletion Retry(TimeSpan delay, string error) => new() { State = JobState.Queued, RetryDelay = delay, Error = error };
    /// <summary>The run was interrupted, for example by shutdown. The job is available again at once and the attempt does not count.</summary>
    public static JobCompletion Release() => new() { State = JobState.Queued, RefundAttempt = true };
}
