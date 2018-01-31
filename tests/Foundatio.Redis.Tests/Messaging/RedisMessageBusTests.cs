using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Messaging;
using Foundatio.Queues;
using Foundatio.Redis.Tests.Extensions;
using Foundatio.Tests.Extensions;
using Foundatio.Tests.Messaging;
using Foundatio.Tests.Queue;
using Foundatio.AsyncEx;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Redis.Tests.Messaging {
    public class RedisMessageBusTests : MessageBusTestBase {
        public RedisMessageBusTests(ITestOutputHelper output) : base(output) {
            var muxer = SharedConnection.GetMuxer();
            muxer.FlushAllAsync().GetAwaiter().GetResult();
        }

        protected override IMessageBus GetMessageBus() {
            return new RedisMessageBus(o => o.Subscriber(SharedConnection.GetMuxer().GetSubscriber()).Topic("test-messages").LoggerFactory(Log));
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

        [Fact]
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
        public override Task CanCancelSubscriptionAsync() {
            return base.CanCancelSubscriptionAsync();
        }

        [Fact]
        public override Task WontKeepMessagesWithNoSubscribersAsync() {
            return base.WontKeepMessagesWithNoSubscribersAsync();
        }

        [Fact]
        public override Task CanReceiveFromMultipleSubscribersAsync() {
            return base.CanReceiveFromMultipleSubscribersAsync();
        }

        [Fact]
        public override void CanDisposeWithNoSubscribersOrPublishers() {
            base.CanDisposeWithNoSubscribersOrPublishers();
        }

        [Fact]
        public async Task CanDisposeCacheAndQueueAndReceiveSubscribedMessages() {
            var muxer = SharedConnection.GetMuxer();
            var messageBus1 = new RedisMessageBus(new RedisMessageBusOptions { Subscriber = muxer.GetSubscriber(), Topic = "test-messages", LoggerFactory = Log });

            var cache = new RedisCacheClient(new RedisCacheClientOptions { ConnectionMultiplexer = muxer });
            Assert.NotNull(cache);

            var queue = new RedisQueue<SimpleWorkItem>(new RedisQueueOptions<SimpleWorkItem> {
                ConnectionMultiplexer = muxer,
                LoggerFactory = Log
            });
            Assert.NotNull(queue);

            using (messageBus1) {
                using (cache) {
                    using (queue) {
                        await cache.SetAsync("test", "test", TimeSpan.FromSeconds(10));
                        await queue.DequeueAsync(new CancellationToken(true));

                        var countdown = new AsyncCountdownEvent(2);
                        await messageBus1.SubscribeAsync<SimpleMessageA>(msg => {
                            Assert.Equal("Hello", msg.Data);
                            countdown.Signal();
                        });

                        await messageBus1.PublishAsync(new SimpleMessageA { Data = "Hello" });
                        await countdown.WaitAsync(TimeSpan.FromSeconds(2));
                        Assert.Equal(1, countdown.CurrentCount);

                        cache.Dispose();
                        queue.Dispose();

                        await messageBus1.PublishAsync(new SimpleMessageA { Data = "Hello" });
                        await countdown.WaitAsync(TimeSpan.FromSeconds(2));
                        Assert.Equal(0, countdown.CurrentCount);
                    }
                }
            }
        }
    }
}