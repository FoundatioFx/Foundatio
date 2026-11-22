using System;
using Foundatio.Messaging.Transport;
using Xunit;

namespace Foundatio.Tests.Messaging;

public class ExperimentalApiTests
{
    [Fact]
    public void PublicApi_WithoutExperimental_DoesNotExposeTheProviderSpi()
    {
        var leaks = ExperimentalApiBoundary.FindLeaks(typeof(InMemoryMessageTransport).Assembly);
        Assert.True(leaks.Count == 0, String.Join(Environment.NewLine, leaks));
    }
}
