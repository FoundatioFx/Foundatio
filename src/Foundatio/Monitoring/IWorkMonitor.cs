using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging.Transport;

namespace Foundatio.Monitoring;

/// <summary>
/// Reads what the messages and jobs of every instance are doing, for a dashboard that polls every few seconds. Each
/// instance shares a snapshot through the cache set with <see cref="FoundatioMonitoringBuilder.UseCache()"/>; without
/// one, the monitor shows only this process. This process's data is always live.
/// </summary>
/// <remarks>
/// Snapshots are written every <see cref="MonitoringOptions.SnapshotInterval"/>, so another instance's data may be that
/// old. Backlog and job counts are read from the transport and job store of this process, and cached for the same
/// interval, so register messaging and jobs on the same transport and store in the process that reads the monitor.
/// </remarks>
/// <example>
/// <code>
/// app.MapGet("/monitor", (IWorkMonitor monitor, CancellationToken ct) => monitor.GetOverviewAsync(cancellationToken: ct));
/// </code>
/// </example>
public interface IWorkMonitor
{
    /// <summary>Returns the instances, active and recent work, stats, backlog and job counts together, reading each snapshot once.</summary>
    /// <param name="filter">Narrows active work, recent work and stats; the instance id also narrows the instances.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<WorkOverview> GetOverviewAsync(WorkFilter? filter = null, CancellationToken cancellationToken = default);

    /// <summary>Returns the instances whose snapshots have not expired, by service name then instance id.</summary>
    Task<IReadOnlyList<WorkInstance>> GetInstancesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the messages being handled and jobs running, oldest first: those that have run for
    /// <see cref="MonitoringOptions.ActiveThreshold"/> or reported progress.
    /// </summary>
    Task<IReadOnlyList<ActiveWork>> GetActiveAsync(WorkFilter? filter = null, CancellationToken cancellationToken = default);

    /// <summary>Returns recently finished work that was listed as active or did not succeed, newest first.</summary>
    Task<IReadOnlyList<CompletedWork>> GetRecentAsync(WorkFilter? filter = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns rolling counts per message type and source and per job name, summed across instances: counts and
    /// in-flight work are added, average durations weighted, and the longest duration is the longest on any instance.
    /// </summary>
    Task<IReadOnlyList<WorkStats>> GetStatsAsync(WorkFilter? filter = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the counts of every queue and durable subscription the instances receive from, read once per source from
    /// this process's transport. A source the transport cannot count, or whose count failed, has only null counts.
    /// Empty when messaging is not configured in this process.
    /// </summary>
    Task<IReadOnlyList<MessageSourceStats>> GetBacklogAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns how many retained jobs are in each state for every job name the instances run, from this process's job
    /// store. Empty when jobs are not configured in this process.
    /// </summary>
    Task<IReadOnlyList<JobCounts>> GetJobCountsAsync(CancellationToken cancellationToken = default);
}
