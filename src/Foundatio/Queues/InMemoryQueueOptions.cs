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
        if (retryDelay == null)
            throw new ArgumentNullException(nameof(retryDelay));

        if (retryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryDelay));

        Target.RetryDelay = retryDelay;
        return this;
    }

    public InMemoryQueueOptionsBuilder<T> CompletedEntryRetentionLimit(int retentionCount)
    {
        if (retentionCount < 0)
            throw new ArgumentOutOfRangeException(nameof(retentionCount));

        Target.CompletedEntryRetentionLimit = retentionCount;
        return this;
    }

    public InMemoryQueueOptionsBuilder<T> RetryMultipliers(int[] multipliers)
    {
        if (multipliers == null)
            throw new ArgumentNullException(nameof(multipliers));

        foreach (int multiplier in multipliers)
        {
            if (multiplier < 1)
                throw new ArgumentOutOfRangeException(nameof(multipliers));
        }

        Target.RetryMultipliers = multipliers;
        return this;
    }
}
