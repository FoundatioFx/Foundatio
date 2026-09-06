using System;
using StackExchange.Redis;

namespace Foundatio.Jobs;

public class RedisJobRuntimeStoreOptions
{
    /// <summary>The Redis connection to use. Required.</summary>
    public IConnectionMultiplexer ConnectionMultiplexer { get; set; } = null!;

    /// <summary>Prefix applied to every key this store creates. Useful to isolate environments/runs on a shared Redis.</summary>
    public string KeyPrefix { get; set; } = "fnd:jobs:";

    /// <summary>Independent active, history, idempotency, dispatch and payload budgets.</summary>
    public JobRuntimeStoreOptions Runtime { get; set; } = new();

    /// <summary>Maximum active jobs; retained terminal history has its own budget.</summary>
    public int MaxJobs { get => Runtime.MaxActiveJobs; set => Runtime = Runtime with { MaxActiveJobs = value }; }

    /// <summary>Time source (defaults to <see cref="TimeProvider.System"/>).</summary>
    public TimeProvider? TimeProvider { get; set; }
}
