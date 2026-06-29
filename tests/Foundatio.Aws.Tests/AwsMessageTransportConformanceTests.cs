using System;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Tests.Messaging;
using Xunit;

namespace Foundatio.Aws.Tests;

/// <summary>
/// Runs the shared transport conformance suite against AWS SQS/SNS. Set the environment variable
/// <c>FOUNDATIO_AWS_CONNECTION_STRING</c> (e.g. <c>serviceurl=http://localhost:4566;accesskey=test;secretkey=test;region=us-east-1</c>
/// for LocalStack, or real AWS credentials) to run; when it is not set every test is skipped. Capabilities SQS/SNS do
/// not support (priority, per-message expiration, push delivery, transport-native dead-letter) are skipped by the base
/// suite via their <c>ISupports*</c> capability checks.
/// </summary>
public class AwsMessageTransportConformanceTests : MessageTransportConformanceTests
{
    // One prefix per test run isolates these queues/topics from prior runs and other environments on the same broker.
    private static readonly string RunPrefix = "fnd-conf-" + Guid.NewGuid().ToString("N")[..8] + "-";

    public AwsMessageTransportConformanceTests(ITestOutputHelper output) : base(output) { }

    protected override IMessageTransport? CreateTransport()
    {
        string? connectionString = Environment.GetEnvironmentVariable("FOUNDATIO_AWS_CONNECTION_STRING");
        if (String.IsNullOrEmpty(connectionString))
            return null; // not configured -> the base suite skips every test

        var options = AwsMessageTransportOptions.FromConnectionString(connectionString);
        options.ResourcePrefix = RunPrefix;
        return new AwsMessageTransport(options);
    }

    [Fact]
    public override Task CanSendAndReceiveBatchAsync() => base.CanSendAndReceiveBatchAsync();

    [Fact]
    public override Task AbandonAsync_RedeliversWithIncrementedDeliveryCountAsync() => base.AbandonAsync_RedeliversWithIncrementedDeliveryCountAsync();

    // CompleteAsync_WithExpiredReceipt is intentionally not run for SQS: DeleteMessage with a stale/used receipt
    // handle is idempotent and does not raise — strict receipt validation is a transport-specific behavior, not part
    // of the shared contract, so only transports that guarantee it (e.g. the in-memory reference) opt in.

    [Fact]
    public override Task SubscribeAsync_DeliversPushMessagesAsync() => base.SubscribeAsync_DeliversPushMessagesAsync();

    [Fact]
    public override Task SendAsync_ToTopic_FansOutToSubscriptionsAsync() => base.SendAsync_ToTopic_FansOutToSubscriptionsAsync();

    [Fact]
    public override Task ReceiveAsync_RespectsPriorityAsync() => base.ReceiveAsync_RespectsPriorityAsync();

    [Fact]
    public override Task SendAsync_WithDeliverAt_DelaysVisibilityAsync() => base.SendAsync_WithDeliverAt_DelaysVisibilityAsync();

    [Fact]
    public override Task DeadLetterAsync_MovesEntryToDeadletterStatsAsync() => base.DeadLetterAsync_MovesEntryToDeadletterStatsAsync();

    [Fact]
    public override Task ReceiveAsync_WithExpiredMessage_DeadlettersAndSkipsAsync() => base.ReceiveAsync_WithExpiredMessage_DeadlettersAndSkipsAsync();

    [Fact]
    public override Task ReceiveAsync_AfterVisibilityTimeout_RedeliversAsync() => base.ReceiveAsync_AfterVisibilityTimeout_RedeliversAsync();

    [Fact]
    public override Task AbandonAsync_WithRedeliveryDelay_RedeliversAfterDelayAsync() => base.AbandonAsync_WithRedeliveryDelay_RedeliversAfterDelayAsync();

    [Fact]
    public override Task RenewLockAsync_ExtendsVisibilityWindowAsync() => base.RenewLockAsync_ExtendsVisibilityWindowAsync();

    [Fact]
    public override Task CompetingConsumers_DoNotReceiveTheSameInFlightMessageAsync() => base.CompetingConsumers_DoNotReceiveTheSameInFlightMessageAsync();

    [Fact]
    public override Task ReceiveDeadLetteredAsync_ReturnsPoisonPayloadAndReasonAsync() => base.ReceiveDeadLetteredAsync_ReturnsPoisonPayloadAndReasonAsync();
}
