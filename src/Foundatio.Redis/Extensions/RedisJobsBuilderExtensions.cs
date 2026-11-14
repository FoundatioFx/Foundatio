using System;
using Foundatio.Jobs;
using Foundatio.Jobs.Store;
using StackExchange.Redis;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers <see cref="RedisJobStore"/> as the store for Foundatio jobs.</summary>
public static class RedisJobsBuilderExtensions
{
    /// <summary>
    /// Stores jobs in Redis, on the <see cref="IConnectionMultiplexer"/> registered in the service collection, so
    /// every instance shares them. The connection stays yours.
    /// </summary>
    /// <param name="builder">The jobs builder.</param>
    /// <param name="options">The store's options.</param>
    /// <exception cref="InvalidOperationException">No <see cref="IConnectionMultiplexer"/> is registered when the store is created.</exception>
    public static FoundatioJobsBuilder UseRedis(this FoundatioJobsBuilder builder, RedisJobStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseStore(sp => Create(GetConnection(sp), options ?? new RedisJobStoreOptions()));
    }

    /// <summary>
    /// Stores jobs in Redis, on the <see cref="IConnectionMultiplexer"/> registered in the service collection, with
    /// options built from the service provider, for example from configuration. The connection stays yours.
    /// </summary>
    /// <param name="builder">The jobs builder.</param>
    /// <param name="options">Builds the store's options.</param>
    /// <exception cref="InvalidOperationException">No <see cref="IConnectionMultiplexer"/> is registered when the store is created.</exception>
    public static FoundatioJobsBuilder UseRedis(this FoundatioJobsBuilder builder, Func<IServiceProvider, RedisJobStoreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        return builder.UseStore(sp => Create(GetConnection(sp), options(sp)));
    }

    /// <summary>Stores jobs in Redis, on <paramref name="connection"/>, so every instance shares them. The connection stays yours.</summary>
    /// <param name="builder">The jobs builder.</param>
    /// <param name="connection">The connection to use.</param>
    /// <param name="options">The store's options.</param>
    public static FoundatioJobsBuilder UseRedis(this FoundatioJobsBuilder builder, IConnectionMultiplexer connection, RedisJobStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(connection);
        return builder.UseStore(_ => Create(connection, options ?? new RedisJobStoreOptions()));
    }

    private static RedisJobStore Create(IConnectionMultiplexer connection, RedisJobStoreOptions? options)
    {
        if (options is null)
            throw new InvalidOperationException("The options factory passed to UseRedis returned null.");

        return new RedisJobStore(connection, options);
    }

    private static IConnectionMultiplexer GetConnection(IServiceProvider services)
    {
        return services.GetService<IConnectionMultiplexer>()
            ?? throw new InvalidOperationException("UseRedis() inside AddFoundatioJobs needs an IConnectionMultiplexer. Register one, for example services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(\"localhost\")), or pass one to UseRedis(connection).");
    }
}
