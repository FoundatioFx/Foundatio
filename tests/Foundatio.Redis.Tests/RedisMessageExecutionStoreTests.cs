using System;
using Foundatio.Messaging;
using Foundatio.Tests.Messaging;

namespace Foundatio.Redis.Tests;

public sealed class RedisMessageExecutionStoreTests : MessageExecutionStoreConformanceTests
{
    protected override IMessageExecutionStore? CreateStore() => RedisTestConnection.Multiplexer is { } connection
        ? new RedisMessageExecutionStore(connection, new RedisMessageExecutionStoreOptions { KeyPrefix = $"native-execution-test:{Guid.NewGuid():N}" })
        : null;
}
