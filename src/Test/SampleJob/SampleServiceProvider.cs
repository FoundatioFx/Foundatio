using System;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Logging;
using Foundatio.Messaging;
using Foundatio.Queues;
using Foundatio.Redis.Metrics;
using Foundatio.ServiceProviders;
using SimpleInjector;
using StackExchange.Redis;

namespace Foundatio.SampleJob {
    public class SampleServiceProvider : BootstrappedServiceProviderBase {
        public SampleServiceProvider() { }
        public SampleServiceProvider(ILoggerFactory loggerFactory) : base(loggerFactory) {}

        protected override IServiceProvider BootstrapInternal(ILoggerFactory loggerFactory) {
            var container = new Container();

            if (loggerFactory != null) {
                container.RegisterSingleton<ILoggerFactory>(loggerFactory);
                container.RegisterSingleton(typeof(ILogger<>), typeof(Logger<>));
            }

            var muxer = ConnectionMultiplexer.Connect("localhost");
            container.RegisterSingleton(muxer);
            var behaviours = new[] { new MetricsQueueBehavior<PingRequest>(new RedisMetricsClient(muxer, loggerFactory: loggerFactory), loggerFactory: loggerFactory) };
            container.RegisterSingleton<IQueue<PingRequest>>(() => new RedisQueue<PingRequest>(muxer, retryDelay: TimeSpan.FromSeconds(1), workItemTimeout: TimeSpan.FromSeconds(5), behaviors: behaviours, loggerFactory: loggerFactory));
            container.RegisterSingleton<ICacheClient>(() => new RedisCacheClient(muxer, loggerFactory: loggerFactory));
            container.RegisterSingleton<IMessageBus>(() => new RedisMessageBus(muxer.GetSubscriber(), loggerFactory: loggerFactory));
            container.RegisterSingleton<ILockProvider>(() => new CacheLockProvider(container.GetInstance<ICacheClient>(), container.GetInstance<IMessageBus>(), loggerFactory));

            return container;
        }
    }
}
