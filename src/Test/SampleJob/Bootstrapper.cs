using System;
using Foundatio.Logging;
using Foundatio.Queues;
using Foundatio.ServiceProviders;
using SimpleInjector;
using StackExchange.Redis;

namespace Foundatio.SampleJob.Jobs {
    public class Bootstrapper : BootstrappedServiceProviderBase {
        protected override IServiceProvider BootstrapInternal(ILoggerFactory loggerFactory) {
            var container = new Container();

            container.RegisterSingleton<ILoggerFactory>(loggerFactory);
            container.RegisterSingleton(typeof(ILogger<>), typeof(Logger<>));

            var muxer = ConnectionMultiplexer.Connect("localhost");
            container.RegisterSingleton(muxer);
            container.RegisterSingleton<IQueue<PingRequest>>(() => new RedisQueue<PingRequest>(muxer));

            return container;
        }
    }
}
