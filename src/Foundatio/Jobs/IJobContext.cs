using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Jobs;

/// <summary>Information about the running job, and a way to report its progress.</summary>
public interface IJobContext
{
    string JobId { get; }
    string JobName { get; }

    /// <summary>The 1-based attempt.</summary>
    int Attempt { get; }

    string? CorrelationId { get; }

    /// <summary>The time the job was due. For a CRON job this is the scheduled occurrence.</summary>
    DateTimeOffset ScheduledFor { get; }

    /// <summary>The CRON schedule that created this job, if any.</summary>
    string? ScheduleName { get; }

    /// <summary>Records progress visible through <see cref="IJobClient.GetAsync"/>. Frequent calls are throttled.</summary>
    Task ReportProgressAsync(int? percent = null, string? message = null, CancellationToken cancellationToken = default);
}
