using System;
using Foundatio.Messaging;
using Foundatio.Tests.Messaging;
using Xunit;

namespace Foundatio.Redis.Tests;

/// <summary>
/// Runs the shared transport conformance suite against the Redis Streams transport. Set
/// <c>FOUNDATIO_REDIS_CONNECTION_STRING</c> (e.g. <c>localhost:6399</c>) to run; skips when unset. A unique key prefix
/// per transport isolates each test's streams. Capabilities Streams does not provide (push delivery, per-message
/// priority, per-message expiration, native scheduled delivery) are skipped by the base suite's capability gates.
/// </summary>
public class RedisStreamsTransportConformanceTests : MessageTransportConformanceTests
{
    public RedisStreamsTransportConformanceTests(ITestOutputHelper output) : base(output) { }

    protected override IMessageTransport? CreateTransport()
    {
        if (RedisTestConnection.Multiplexer is not { } connection)
            return null; // not configured -> the base suite skips every test

        return new RedisStreamsMessageTransport(new RedisStreamsMessageTransportOptions
        {
            ConnectionMultiplexer = connection,
            KeyPrefix = $"fnd-conf:{Guid.NewGuid():N}:"
        });
    }

}
