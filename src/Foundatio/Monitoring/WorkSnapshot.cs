using System;
using System.Collections.Generic;

namespace Foundatio.Monitoring;

/// <summary>What a <see cref="WorkTracker"/> holds at one moment.</summary>
internal sealed class WorkSnapshot
{
    /// <summary>Work running past the threshold, or that reported progress, oldest first.</summary>
    public IReadOnlyList<ActiveWorkEntry> Active { get; init; } = [];

    /// <summary>Recently finished work that was visible or failed, newest first.</summary>
    public IReadOnlyList<CompletedWorkEntry> Recent { get; init; } = [];

    /// <summary>Counts over <see cref="StatsWindow"/> for every message type and job name that saw work in it.</summary>
    public IReadOnlyList<WorkStatsEntry> Stats { get; init; } = [];

    /// <summary>How far back <see cref="Stats"/> reach.</summary>
    public TimeSpan StatsWindow { get; init; }
}
