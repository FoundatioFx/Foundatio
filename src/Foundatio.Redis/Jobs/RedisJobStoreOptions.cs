namespace Foundatio.Jobs.Store;

/// <summary>Settings for <see cref="RedisJobStore"/>.</summary>
public sealed record RedisJobStoreOptions
{
    /// <summary>Prepended to every key and used as the store's hash tag, so the whole store lives on one cluster slot.</summary>
    public string Prefix { get; init; } = "foundatio:jobs";

    /// <summary>The Redis database to use, or -1 for the connection's default.</summary>
    public int Database { get; init; } = -1;
}
