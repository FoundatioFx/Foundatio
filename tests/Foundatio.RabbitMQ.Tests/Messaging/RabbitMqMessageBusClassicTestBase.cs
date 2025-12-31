using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Tests.Messaging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.RabbitMQ.Tests.Messaging;

public abstract class RabbitMqMessageBusClassicTestBase : RabbitMqMessageBusTestBase
{
    public RabbitMqMessageBusClassicTestBase(string connectionString, ITestOutputHelper output) : base(connectionString, output)
    {
    }


    protected override IMessageBus GetMessageBus(Func<SharedMessageBusOptions, SharedMessageBusOptions> config = null)
    {
        return new RabbitMQMessageBus(o =>
        {
            o.ConnectionString(ConnectionString);
            o.LoggerFactory(Log);

            config?.Invoke(o.Target);

            return o;
        });
    }

    [Fact]
    public override async Task CanHandlePoisonedMessageWithAutomaticAcknowledgementsAsync()
    {
        await using var messageBus = new RabbitMQMessageBus(o => o
            .ConnectionString(ConnectionString)
            .LoggerFactory(Log)
            .AcknowledgementStrategy(AcknowledgementStrategy.Automatic));

        long handlerInvocations = 0;

        try
        {
            await messageBus.SubscribeAsync<SimpleMessageA>(_ =>
            {
                _logger.LogTrace("SimpleAMessage received");
                Interlocked.Increment(ref handlerInvocations);
                throw new Exception("Poisoned message");
            }, TestCancellationToken);

            await messageBus.PublishAsync(new SimpleMessageA());
            _logger.LogTrace("Published one...");

            await Task.Delay(TimeSpan.FromSeconds(3), TestCancellationToken);
            Assert.Equal(3, handlerInvocations);
        }
        finally
        {
            await CleanupMessageBusAsync(messageBus);
        }
    }
}
