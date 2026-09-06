using System;

namespace Foundatio.Jobs;

/// <summary>Independent budgets for executable work, history, idempotency and delayed messaging.</summary>
public sealed record JobRuntimeStoreOptions
{
    public int MaxActiveJobs { get; init; } = 100000;
    public int MaxHistoryJobs { get; init; } = 100000;
    public TimeSpan HistoryRetention { get; init; } = TimeSpan.FromDays(7);
    /// <summary>Minimum time an ID remains reserved after completion, including after history eviction.</summary>
    public TimeSpan DeduplicationRetention { get; init; } = TimeSpan.FromDays(7);
    public int MaxDeduplicationRecords { get; init; } = 1000000;
    public int MaxScheduledDispatches { get; init; } = 100000;
    public int MaxPayloadBytes { get; init; } = 1048576;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxActiveJobs, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxHistoryJobs);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDeduplicationRecords, MaxActiveJobs);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxScheduledDispatches, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPayloadBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(HistoryRetention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(DeduplicationRetention, HistoryRetention);
    }
}

/// <summary>Current usage for admission and retention monitoring.</summary>
public sealed record JobRuntimeStoreStats(long ActiveJobs, long HistoryJobs, long DeduplicationRecords, long ScheduledDispatches);
