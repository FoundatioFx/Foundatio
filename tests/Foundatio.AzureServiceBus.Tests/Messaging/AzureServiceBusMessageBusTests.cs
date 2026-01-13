using System;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Tests.Messaging;
using Foundatio.Tests.Utility;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.AzureServiceBus.Tests.Messaging;

public class AzureServiceBusMessageBusTests : MessageBusTestBase
{
    // Azure Service Bus Emulator limitations documentation:
    // https://learn.microsoft.com/en-us/azure/service-bus-messaging/test-locally-with-service-bus-emulator
    // The emulator has connection quota limits and delayed message delivery may take longer than expected.
    private const string EmulatorLimitationsUrl = "https://learn.microsoft.com/en-us/azure/service-bus-messaging/test-locally-with-service-bus-emulator";

    private static readonly bool _isEmulator = IsEmulator();

    private static bool IsEmulator()
    {
        string connectionString = Configuration.GetConnectionString("AzureServiceBusConnectionString");
        return !String.IsNullOrEmpty(connectionString) &&
            connectionString.Contains("UseDevelopmentEmulator=true", StringComparison.OrdinalIgnoreCase);
    }

    public AzureServiceBusMessageBusTests(ITestOutputHelper output) : base(output) { }

    protected override IMessageBus GetMessageBus(Func<SharedMessageBusOptions, SharedMessageBusOptions> config = null)
    {
        string connectionString = Configuration.GetConnectionString("AzureServiceBusConnectionString");
        if (String.IsNullOrEmpty(connectionString))
            return null;

        return new AzureServiceBusMessageBus(o =>
        {
            o.ConnectionString(connectionString);
            o.Topic("test-messages");

            if (_isEmulator)
                o.SubscriptionName("test-subscription");

            // These options are not supported by the emulator
            if (!_isEmulator)
            {
                o.TopicEnableBatchedOperations(true);
                o.TopicEnablePartitioning(true);
                o.TopicSupportOrdering(false);
                o.TopicRequiresDuplicateDetection(false);
                o.SubscriptionAutoDeleteOnIdle(TimeSpan.FromMinutes(5));
                o.SubscriptionEnableBatchedOperations(true);
                o.SubscriptionMaxDeliveryCount(Int32.MaxValue);
            }

            o.PrefetchCount(500);
            o.LoggerFactory(Log);

            config?.Invoke(o.Target);

            return o;
        });
    }

    [Fact]
    public override Task CanUseMessageOptionsAsync()
    {
        return base.CanUseMessageOptionsAsync();
    }

    [Fact]
    public override Task CanSendMessageAsync()
    {
        return base.CanSendMessageAsync();
    }

    [Fact]
    public override Task CanHandleNullMessageAsync()
    {
        return base.CanHandleNullMessageAsync();
    }

    [Fact]
    public override Task CanSendDerivedMessageAsync()
    {
        return base.CanSendDerivedMessageAsync();
    }

    [Fact]
    public override Task CanSendMappedMessageAsync()
    {
        return base.CanSendMappedMessageAsync();
    }

    [Fact]
    public override async Task CanSendDelayedMessageAsync()
    {
        // Skip this test when using the emulator - the emulator's scheduled message delivery
        // is significantly slower than the cloud service. The test sends 2500 messages with
        // 0-100ms delays and expects all to be received within 30 seconds. The emulator
        // typically takes 60+ seconds due to its single-threaded message scheduling.
        // See emulator quotas: https://learn.microsoft.com/en-us/azure/service-bus-messaging/overview-emulator#usage-quotas
        if (_isEmulator)
        {
            _logger.LogWarning("Skipping {TestName}: Azure Service Bus Emulator scheduled message delivery is too slow for this test (60+ seconds vs 30 second timeout). See {Url}",
                nameof(CanSendDelayedMessageAsync), EmulatorLimitationsUrl);
            return;
        }

        await base.CanSendDelayedMessageAsync();
    }

    [Fact]
    public override Task CanSubscribeConcurrentlyAsync()
    {
        return base.CanSubscribeConcurrentlyAsync();
    }

    [Fact]
    public override async Task CanReceiveMessagesConcurrentlyAsync()
    {
        // Skip this test when using the emulator - the emulator has a limit of 10 concurrent
        // connections per namespace, which this test exceeds by creating multiple message bus instances.
        // See emulator quotas: https://learn.microsoft.com/en-us/azure/service-bus-messaging/overview-emulator#usage-quotas
        // "Number of concurrent connections to namespace: 10"
        if (_isEmulator)
        {
            _logger.LogWarning("Skipping {TestName}: Azure Service Bus Emulator has a limit of 10 concurrent connections per namespace. See {Url}",
                nameof(CanReceiveMessagesConcurrentlyAsync), EmulatorLimitationsUrl);
            return;
        }

        await base.CanReceiveMessagesConcurrentlyAsync();
    }

    [Fact]
    public override Task CanSendMessageToMultipleSubscribersAsync()
    {
        return base.CanSendMessageToMultipleSubscribersAsync();
    }

    [Fact]
    public override Task CanTolerateSubscriberFailureAsync()
    {
        return base.CanTolerateSubscriberFailureAsync();
    }

    [Fact]
    public override Task WillOnlyReceiveSubscribedMessageTypeAsync()
    {
        return base.WillOnlyReceiveSubscribedMessageTypeAsync();
    }

    [Fact]
    public override Task WillReceiveDerivedMessageTypesAsync()
    {
        return base.WillReceiveDerivedMessageTypesAsync();
    }

    [Fact]
    public override Task CanSubscribeToAllMessageTypesAsync()
    {
        return base.CanSubscribeToAllMessageTypesAsync();
    }

    [Fact]
    public override Task CanSubscribeToRawMessagesAsync()
    {
        return base.CanSubscribeToRawMessagesAsync();
    }

    [Fact]
    public override Task CanCancelSubscriptionAsync()
    {
        return base.CanCancelSubscriptionAsync();
    }

    [Fact]
    public override async Task WontKeepMessagesWithNoSubscribersAsync()
    {
        // Skip this test when using the emulator - the emulator requires subscriptions to be
        // pre-configured. The test publishes a message BEFORE subscribing and expects the message
        // NOT to be received. But since the subscription already exists in the emulator config,
        // it receives the message. This is a fundamental limitation of the emulator.
        // See emulator limitations: https://learn.microsoft.com/en-us/azure/service-bus-messaging/overview-emulator#known-limitations
        // "It doesn't support on-the-fly management operations through a client-side SDK."
        if (_isEmulator)
        {
            _logger.LogWarning("Skipping {TestName}: Azure Service Bus Emulator requires pre-configured subscriptions. The subscription receives messages even before the test subscribes. See {Url}",
                nameof(WontKeepMessagesWithNoSubscribersAsync), EmulatorLimitationsUrl);
            return;
        }

        await base.WontKeepMessagesWithNoSubscribersAsync();
    }

    [Fact]
    public override async Task CanReceiveFromMultipleSubscribersAsync()
    {
        // Skip this test when using the emulator - the emulator requires subscriptions to be
        // pre-configured and doesn't support creating them dynamically. All message bus instances
        // in the test share the same subscription, causing them to compete for messages rather
        // than each receiving a copy. This is a fundamental limitation of the emulator.
        // See emulator limitations: https://learn.microsoft.com/en-us/azure/service-bus-messaging/overview-emulator#known-limitations
        // "It doesn't support on-the-fly management operations through a client-side SDK."
        if (_isEmulator)
        {
            _logger.LogWarning("Skipping {TestName}: Azure Service Bus Emulator requires pre-configured subscriptions and doesn't support dynamic subscription creation. See {Url}",
                nameof(CanReceiveFromMultipleSubscribersAsync), EmulatorLimitationsUrl);
            return;
        }

        await base.CanReceiveFromMultipleSubscribersAsync();
    }

    [Fact]
    public override void CanDisposeWithNoSubscribersOrPublishers()
    {
        base.CanDisposeWithNoSubscribersOrPublishers();
    }

    [Fact]
    public override Task CanHandlePoisonedMessageAsync()
    {
        return base.CanHandlePoisonedMessageAsync();
    }
}
