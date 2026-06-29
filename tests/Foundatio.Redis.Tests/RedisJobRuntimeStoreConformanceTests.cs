using System;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Tests.Jobs;
using StackExchange.Redis;
using Xunit;

namespace Foundatio.Redis.Tests;

/// <summary>
/// Runs the shared <see cref="IJobRuntimeStore"/> conformance suite against a real Redis. Set
/// <c>FOUNDATIO_REDIS_CONNECTION_STRING</c> (e.g. <c>localhost:6399</c> for the bundled docker-compose Redis) to run;
/// when it is not set every test is skipped. Each test gets a unique key prefix so runs never collide.
/// </summary>
public class RedisJobRuntimeStoreConformanceTests : JobRuntimeStoreConformanceTests
{
    private static readonly Lazy<IConnectionMultiplexer?> SharedConnection = new(() =>
    {
        string? connectionString = Environment.GetEnvironmentVariable("FOUNDATIO_REDIS_CONNECTION_STRING");
        return String.IsNullOrEmpty(connectionString) ? null : ConnectionMultiplexer.Connect(connectionString);
    });

    public RedisJobRuntimeStoreConformanceTests(ITestOutputHelper output) : base(output) { }

    protected override IJobRuntimeStore? CreateStore(TimeProvider timeProvider)
    {
        if (SharedConnection.Value is not { } connection)
            return null; // not configured -> the base suite skips every test

        return new RedisJobRuntimeStore(new RedisJobRuntimeStoreOptions
        {
            ConnectionMultiplexer = connection,
            KeyPrefix = $"fnd-conf:{Guid.NewGuid():N}:",
            TimeProvider = timeProvider
        });
    }

    [Fact]
    public override Task JobLifecycle_RoundTripsAndTransitionsAsync() => base.JobLifecycle_RoundTripsAndTransitionsAsync();

    [Fact]
    public override Task Query_FiltersByNameStatusAndLimitAsync() => base.Query_FiltersByNameStatusAndLimitAsync();

    [Fact]
    public override Task Leasing_ClaimRenewReleaseAndStealAsync() => base.Leasing_ClaimRenewReleaseAndStealAsync();

    [Fact]
    public override Task StaleRecovery_ReclaimsExpiredButNotLiveOrCronAsync() => base.StaleRecovery_ReclaimsExpiredButNotLiveOrCronAsync();

    [Fact]
    public override Task ScheduledDispatches_ClaimCompleteAndRescheduleAsync() => base.ScheduledDispatches_ClaimCompleteAndRescheduleAsync();

    [Fact]
    public override Task Concurrency_OptimisticControlElectsSingleWinnerAsync() => base.Concurrency_OptimisticControlElectsSingleWinnerAsync();
}
