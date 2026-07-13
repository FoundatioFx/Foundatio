using System;
using Foundatio.Jobs;
using Foundatio.Tests.Jobs;
using Xunit;

namespace Foundatio.Redis.Tests;

/// <summary>
/// Runs the shared <see cref="IJobRuntimeStore"/> conformance suite against a real Redis. Set
/// <c>FOUNDATIO_REDIS_CONNECTION_STRING</c> (e.g. <c>localhost:6399</c> for the bundled docker-compose Redis) to run;
/// when it is not set every test is skipped. Each test gets a unique key prefix so runs never collide. Inheriting the
/// base [Fact]s means a new conformance check automatically runs against Redis with no override to forget.
/// </summary>
public class RedisJobRuntimeStoreConformanceTests : JobRuntimeStoreConformanceTests
{
    public RedisJobRuntimeStoreConformanceTests(ITestOutputHelper output) : base(output) { }

    protected override IJobRuntimeStore? CreateStore(TimeProvider timeProvider) =>
        RedisTestConnection.Multiplexer is { } connection
            ? RedisTestConnection.CreateStore(connection, timeProvider)
            : null; // not configured -> the base suite skips every test
}
