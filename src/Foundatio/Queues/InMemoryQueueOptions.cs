using System;

namespace Foundatio.Queues;

public class InMemoryQueueOptions<T> : SharedQueueOptions<T> where T : class
{
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(1);
    public int CompletedEntryRetentionLimit { get; set; } = 100;
    public int[] RetryMultipliers { get; set; } = { 1, 3, 5, 10 };
}

public class InMemoryQueueOptionsBuilder<T> : SharedQueueOptionsBuilder<T, InMemoryQueueOptions<T>, InMemoryQueueOptionsBuilder<T>> where T : class
{
    public InMemoryQueueOptionsBuilder<T> RetryDelay(TimeSpan retryDelay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retryDelay, TimeSpan.Zero);

        Target.RetryDelay = retryDelay;
        return this;
    }

    public InMemoryQueueOptionsBuilder<T> CompletedEntryRetentionLimit(int retentionCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retentionCount);

        Target.CompletedEntryRetentionLimit = retentionCount;
        return this;
    }

    public InMemoryQueueOptionsBuilder<T> RetryMultipliers(int[] multipliers)
    {
        ArgumentNullException.ThrowIfNull(multipliers);

        foreach (int multiplier in multipliers)
        {
            if (multiplier < 1)
                throw new ArgumentOutOfRangeException(nameof(multipliers));
        }

        Target.RetryMultipliers = multipliers;
        return this;
    }
}
