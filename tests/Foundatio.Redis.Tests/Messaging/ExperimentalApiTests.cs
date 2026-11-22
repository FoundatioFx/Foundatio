using System;
using Foundatio.Messaging.Transport;
using Xunit;

namespace Foundatio.Redis.Tests.Messaging;

public class ExperimentalApiTests
{
    [Fact]
    public void PublicApi_WithoutExperimental_DoesNotExposeTheProviderSpi()
    {
        var leaks = ExperimentalApiBoundary.FindLeaks(typeof(RedisMessageTransport).Assembly);
        Assert.True(leaks.Count == 0, String.Join(Environment.NewLine, leaks));
    }
}
