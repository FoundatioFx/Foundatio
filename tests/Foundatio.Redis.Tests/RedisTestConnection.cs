using System;
using Foundatio.Jobs;
using StackExchange.Redis;

namespace Foundatio.Redis.Tests;

/// <summary>
/// Shared, lazily-opened Redis connection for the Redis test suites. Gated on
/// <c>FOUNDATIO_REDIS_CONNECTION_STRING</c> (e.g. <c>localhost:6399</c> for the bundled docker-compose Redis); when it
/// is unset <see cref="Multiplexer"/> is null and the suites skip every test.
/// </summary>
internal static class RedisTestConnection
{
    private static readonly Lazy<IConnectionMultiplexer?> Shared = new(() =>
    {
        string? connectionString = Environment.GetEnvironmentVariable("FOUNDATIO_REDIS_CONNECTION_STRING");
        return String.IsNullOrEmpty(connectionString) ? null : ConnectionMultiplexer.Connect(connectionString);
    });

    public static IConnectionMultiplexer? Multiplexer => Shared.Value;

    /// <summary>Creates a store under a unique key prefix so concurrent tests and leftover keys never collide.</summary>
    public static RedisJobRuntimeStore CreateStore(IConnectionMultiplexer connection, TimeProvider? timeProvider = null) =>
        new(new RedisJobRuntimeStoreOptions
        {
            ConnectionMultiplexer = connection,
            KeyPrefix = $"fnd-it:{Guid.NewGuid():N}:",
            TimeProvider = timeProvider
        });
}
