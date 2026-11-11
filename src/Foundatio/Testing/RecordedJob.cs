using Foundatio.Jobs;

namespace Foundatio.Testing;

/// <summary>One finished run of a job, recorded by <see cref="FoundatioTestHarness"/>.</summary>
public sealed record RecordedJob
{
    public required string JobId { get; init; }
    public required string JobName { get; init; }

    /// <summary>The job's arguments, or null for a job without arguments.</summary>
    public object? Arguments { get; init; }

    /// <summary>
    /// <see cref="JobStatus.Succeeded"/>, <see cref="JobStatus.Failed"/> or <see cref="JobStatus.Cancelled"/>; or
    /// <see cref="JobStatus.Queued"/> when the run failed and will be retried, or was interrupted by shutdown.
    /// </summary>
    public required JobStatus Status { get; init; }

    /// <summary>The attempt this run was, starting at 1.</summary>
    public int Attempt { get; init; }

    /// <summary>The result or cancellation message.</summary>
    public string? Message { get; init; }

    public string? Error { get; init; }
    public string? CorrelationId { get; init; }

    /// <summary>The CRON schedule that created the job, if any.</summary>
    public string? ScheduleName { get; init; }
}
