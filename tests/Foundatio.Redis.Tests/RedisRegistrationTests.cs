using System;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace Foundatio.Redis.Tests;

public class RedisRegistrationTests
{
    [Fact]
    public void UseRedis_ConflictingExplicitConnections_FailsBeforeConnecting()
    {
        var builder = new ServiceCollection().AddFoundatio();
        builder.Messaging.UseRedis(connectionString: "localhost:6379");
        var ex = Assert.Throws<ArgumentException>(() => builder.Jobs.UseRedis(connectionString: "localhost:6380"));
        Assert.Contains("share one Redis connection", ex.Message);
        Assert.DoesNotContain("6380", ex.Message);
    }

    [Fact]
    public void UseRedis_ExistingConnection_RejectsIgnoredConnectionString()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionMultiplexer>(_ => throw new InvalidOperationException("Must not connect during registration."));
        var builder = services.AddFoundatio();
        builder.Messaging.UseRedis();
        var ex = Assert.Throws<ArgumentException>(() => builder.Jobs.UseRedis(connectionString: "localhost:6380"));
        Assert.Contains("already registered", ex.Message);
        Assert.DoesNotContain("6380", ex.Message);
    }

    [Fact]
    public void UseRedis_DefaultThenExplicitConnection_AllowsOneSharedSetting()
    {
        var builder = new ServiceCollection().AddFoundatio();
        builder.Messaging.UseRedis();
        builder.Jobs.UseRedis(connectionString: "localhost:6380");
        builder.Messaging.UseRedis(connectionString: "localhost:6380");
        Assert.Throws<ArgumentException>(() => builder.Jobs.UseRedis(connectionString: "localhost:6379"));
    }
}
