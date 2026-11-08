using System;

namespace Foundatio.Jobs;

/// <summary>The stored state of a job.</summary>
public sealed record JobInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public JobStatus Status { get; init; }

    /// <summary>The serialized arguments, empty for jobs without arguments.</summary>
    public ReadOnlyMemory<byte> Payload { get; init; }

    public string? CorrelationId { get; init; }

    /// <summary>The CRON schedule that created the job, if any.</summary>
    public string? ScheduleName { get; init; }

    /// <summary>How many times the job has started, including the current run.</summary>
    public int Attempt { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The job is not started before this time.</summary>
    public DateTimeOffset AvailableAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>The last reported progress percentage.</summary>
    public int? Progress { get; init; }

    /// <summary>The last progress or result message.</summary>
    public string? Message { get; init; }

    /// <summary>The error from the most recent failed attempt.</summary>
    public string? Error { get; init; }

    public bool CancellationRequested { get; init; }

    /// <summary>The worker running the job.</summary>
    public string? WorkerId { get; init; }

    /// <summary>Identifies the current run. Only the worker holding it may update or complete the job.</summary>
    public string? ClaimToken { get; init; }

    public DateTimeOffset? LeaseExpiresAt { get; init; }
}
