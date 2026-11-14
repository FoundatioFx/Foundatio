using System;
using Foundatio.Messaging.Transport;
using Xunit;

namespace Foundatio.Tests.Messaging;

/// <summary>The conformance suite against the in-memory transport emulating an SQS standard queue: no ordered groups or deduplication, and SQS's limits.</summary>
public class InMemoryMessageTransportLikeSqsStandardTests(ITestOutputHelper output) : InMemoryMessageTransportTests(output)
{
    protected override TransportCapabilities EmulatedCapabilities { get; } = new()
    {
        MaxDelay = TimeSpan.FromMinutes(15),
        MaxPublishDelay = TimeSpan.FromDays(14),
        MaxRetryDelay = TimeSpan.FromHours(12),
        MinRetryDelay = TimeSpan.FromSeconds(1),
        MaxSendBatchSize = 10,
        MaxReceiveBatchSize = 10,
        MaxMessageSize = 256 * 1024,
        MaxLeaseDuration = TimeSpan.FromHours(12)
    };
}
