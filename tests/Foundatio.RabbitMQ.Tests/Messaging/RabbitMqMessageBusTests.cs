using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Tests.Messaging;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.RabbitMQ.Tests.Messaging {
    public class RabbitMqMessageBusTests : MessageBusTestBase {
        public RabbitMqMessageBusTests(ITestOutputHelper output) : base(output) { }
        protected override IMessageBus GetMessageBus(Func<SharedMessageBusOptions, SharedMessageBusOptions> config = null) {
            return new RabbitMQMessageBus(o => {
                o.ConnectionString("amqp://localhost");
                o.LoggerFactory(Log);

                config?.Invoke(o.Target);

                return o;
            });
        }

        [Fact]
        public override Task CanSendMessageAsync() {
            return base.CanSendMessageAsync();
        }

        [Fact]
        public override Task CanHandleNullMessageAsync() {
            return base.CanHandleNullMessageAsync();
        }

        [Fact]
        public override Task CanSendDerivedMessageAsync() {
            return base.CanSendDerivedMessageAsync();
        }

        [Fact]
        public override Task CanSendDelayedMessageAsync() {
            return base.CanSendDelayedMessageAsync();
        }

        [Fact]
        public override Task CanSubscribeConcurrentlyAsync() {
            return base.CanSubscribeConcurrentlyAsync();
        }

        [Fact(Skip = "TODO: Ensure this is not broken")]
        public override Task CanReceiveMessagesConcurrentlyAsync() {
            return base.CanReceiveMessagesConcurrentlyAsync();
        }

        [Fact]
        public override Task CanSendMessageToMultipleSubscribersAsync() {
            return base.CanSendMessageToMultipleSubscribersAsync();
        }

        [Fact]
        public override Task CanTolerateSubscriberFailureAsync() {
            return base.CanTolerateSubscriberFailureAsync();
        }

        [Fact]
        public override Task WillOnlyReceiveSubscribedMessageTypeAsync() {
            return base.WillOnlyReceiveSubscribedMessageTypeAsync();
        }

        [Fact]
        public override Task WillReceiveDerivedMessageTypesAsync() {
            return base.WillReceiveDerivedMessageTypesAsync();
        }

        [Fact]
        public override Task CanSubscribeToAllMessageTypesAsync() {
            return base.CanSubscribeToAllMessageTypesAsync();
        }

        [Fact]
        public override Task CanSubscribeToRawMessagesAsync() {
            return base.CanSubscribeToRawMessagesAsync();
        }

        [Fact]
        public override Task CanCancelSubscriptionAsync() {
            return base.CanCancelSubscriptionAsync();
        }

        [Fact]
        public override Task WontKeepMessagesWithNoSubscribersAsync() {
            return base.WontKeepMessagesWithNoSubscribersAsync();
        }

        [Fact(Skip = "TODO: Ensure this is not broken")]
        public override Task CanReceiveFromMultipleSubscribersAsync() {
            return base.CanReceiveFromMultipleSubscribersAsync();
        }

        [Fact]
        public override void CanDisposeWithNoSubscribersOrPublishers() {
            base.CanDisposeWithNoSubscribersOrPublishers();
        }
        
        [Fact]
        public void MessageQueueWillPersistAndNotLoseMessages() {
            Log.MinimumLevel = LogLevel.Trace;
            var messageBus1 = new RabbitMQMessageBus(o => o
                .ConnectionString("amqp://localhost:5673")
                .LoggerFactory(Log)
                .SubscriptionQueueName("MyQueue")
                .IsSubscriptionQueueExclusive(false)
                .SubscriptionQueueAutoDelete(false)
                .AcknowledgementStrategy(AcknowledgementStrategy.Automatic));
            var t = new AutoResetEvent(false);
            var cts = new CancellationTokenSource();
            messageBus1.SubscribeAsync<SimpleMessageA>(msg => {
                _logger.LogTrace("Got message {Data}", msg.Data);
                t.Set();
            }, cts.Token);
            messageBus1.PublishAsync(new SimpleMessageA {Data = "Audit message 1"});
            t.WaitOne(TimeSpan.FromSeconds(5));
            cts.Cancel();
            
            messageBus1.PublishAsync(new SimpleMessageA {Data = "Audit message 2"});

            cts = new CancellationTokenSource();
            messageBus1.SubscribeAsync<SimpleMessageA>(msg => {
                _logger.LogTrace("Got message {Data}", msg.Data);
                t.Set();
            }, cts.Token);
            t.WaitOne(TimeSpan.FromSeconds(5));
            cts.Cancel();
            
            messageBus1.PublishAsync(new SimpleMessageA {Data = "Audit offline message 1"});
            messageBus1.PublishAsync(new SimpleMessageA {Data = "Audit offline message 2"});
            messageBus1.PublishAsync(new SimpleMessageA {Data = "Audit offline message 3"});
            
            messageBus1.Dispose();
            var messageBus2 = new RabbitMQMessageBus(o => o
                .ConnectionString("amqp://localhost:5673")
                .LoggerFactory(Log)
                .SubscriptionQueueName("MyQueue")
                .IsSubscriptionQueueExclusive(false)
                .SubscriptionQueueAutoDelete(false)
                .AcknowledgementStrategy(AcknowledgementStrategy.Automatic));
            cts = new CancellationTokenSource();
            messageBus2.SubscribeAsync<SimpleMessageA>(msg => {
                _logger.LogTrace("Got message {Data}", msg.Data);
                t.Set();
            }, cts.Token);
            messageBus2.PublishAsync(new SimpleMessageA {Data = "Another audit message 4"});
        }
    }
}
