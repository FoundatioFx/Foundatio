using System;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Metrics;
using Foundatio.Queues;
using Microsoft.Extensions.Logging;
using SimpleInjector;
using StackExchange.Redis;

namespace Foundatio.SampleJob {
    public class SampleServiceProvider {
        public static IServiceProvider Create(ILoggerFactory loggerFactory) {
            var container = new Container();

            if (loggerFactory != null) {
                container.RegisterSingleton<ILoggerFactory>(loggerFactory);
                container.RegisterSingleton(typeof(ILogger<>), typeof(Logger<>));
            }

            var muxer = ConnectionMultiplexer.Connect("localhost");
            container.RegisterSingleton(muxer);
            var behaviours = new[] { new MetricsQueueBehavior<PingRequest>(new RedisMetricsClient(new RedisMetricsClientOptions { ConnectionMultiplexer = muxer, LoggerFactory = loggerFactory }), loggerFactory: loggerFactory) };
            container.RegisterSingleton<IQueue<PingRequest>>(() => new RedisQueue<PingRequest>(new RedisQueueOptions<PingRequest> { ConnectionMultiplexer = muxer, RetryDelay = TimeSpan.FromSeconds(1), WorkItemTimeout = TimeSpan.FromSeconds(5), Behaviors = behaviours, LoggerFactory = loggerFactory }));
            container.RegisterSingleton<ICacheClient>(() => new RedisCacheClient(new RedisCacheClientOptions { ConnectionMultiplexer = muxer, LoggerFactory = loggerFactory }));
            container.RegisterSingleton<IMessageBus>(() => new RedisMessageBus(new RedisMessageBusOptions { Subscriber = muxer.GetSubscriber(), LoggerFactory = loggerFactory }));
            container.RegisterSingleton<ILockProvider>(() => new CacheLockProvider(container.GetInstance<ICacheClient>(), container.GetInstance<IMessageBus>(), loggerFactory));

            return container;
        }
    }
}