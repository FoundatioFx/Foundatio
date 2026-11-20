using System;
using System.Collections.Generic;
using Foundatio.Messaging.Transport;

namespace Foundatio.Monitoring;

/// <summary>Everything a dashboard page shows, read in one call to <see cref="IWorkMonitor.GetOverviewAsync"/>.</summary>
public sealed record WorkOverview
{
    /// <summary>When the overview was put together, by this process's clock.</summary>
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>The instances, by service name then instance id.</summary>
    public IReadOnlyList<WorkInstance> Instances { get; init; } = [];

    /// <summary>Work running now, oldest first.</summary>
    public IReadOnlyList<ActiveWork> Active { get; init; } = [];

    /// <summary>Recently finished work, newest first.</summary>
    public IReadOnlyList<CompletedWork> Recent { get; init; } = [];

    /// <summary>Rolling counts summed across instances.</summary>
    public IReadOnlyList<WorkStats> Stats { get; init; } = [];

    /// <summary>The counts of every queue and durable subscription the instances receive from.</summary>
    public IReadOnlyList<MessageSourceStats> Backlog { get; init; } = [];

    /// <summary>Job counts by state for every job name the instances run.</summary>
    public IReadOnlyList<JobCounts> Jobs { get; init; } = [];
}
