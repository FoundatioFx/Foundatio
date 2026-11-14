using System;

namespace Foundatio.Jobs;

/// <summary>Information about the running job, and a way to report its progress.</summary>
/// <remarks>
/// <see cref="IHandlerContext.Attempt"/> is the job's attempt and <see cref="IHandlerContext.Headers"/> the headers it
/// was enqueued with. <see cref="IHandlerContext.ReportProgressAsync"/> also saves the progress to the job, where
/// <see cref="IJobClient.GetAsync"/> sees it. A store error does not fail the job: it is logged, and the report is
/// saved with the next one or when the job finishes.
/// </remarks>
public interface IJobContext : IHandlerContext
{
    /// <summary>The job's id, the same on every attempt.</summary>
    string JobId { get; }

    /// <summary>The job's stable name.</summary>
    string JobName { get; }

    /// <summary>The time the job was due. For a CRON job this is the scheduled occurrence.</summary>
    DateTimeOffset ScheduledFor { get; }

    /// <summary>The CRON schedule that created this job, if any.</summary>
    string? ScheduleName { get; }
}
