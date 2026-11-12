using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Jobs;

/// <summary>Information about the running job, and a way to report its progress.</summary>
public interface IJobContext
{
    /// <summary>The job's id, the same on every attempt.</summary>
    string JobId { get; }

    /// <summary>The job's stable name.</summary>
    string JobName { get; }

    /// <summary>The 1-based attempt.</summary>
    int Attempt { get; }

    /// <summary>Links the job to the request, message or job that enqueued it.</summary>
    string? CorrelationId { get; }

    /// <summary>The time the job was due. For a CRON job this is the scheduled occurrence.</summary>
    DateTimeOffset ScheduledFor { get; }

    /// <summary>The CRON schedule that created this job, if any.</summary>
    string? ScheduleName { get; }

    /// <summary>Headers the job was enqueued with, including the W3C trace context. Empty when there are none.</summary>
    IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>
    /// Records progress visible through <see cref="IJobClient.GetAsync"/>. Frequent calls are throttled, and calls from
    /// parallel branches are safe. A store error does not fail the job: it is logged, and the report is saved with the
    /// next one or when the job finishes.
    /// </summary>
    Task ReportProgressAsync(int? percent = null, string? message = null, CancellationToken cancellationToken = default);
}
