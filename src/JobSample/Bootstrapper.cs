using System;
using Foundatio.Queues;
using Foundatio.ServiceProviders;
using SimpleInjector;
using StackExchange.Redis;

namespace Foundatio.JobSample.Jobs {
    public class Bootstrapper : BootstrappedServiceProviderBase {
        protected override IServiceProvider BootstrapInternal() {
            var container = new Container();

            var muxer = ConnectionMultiplexer.Connect("localhost");
            container.RegisterSingleton(muxer);
            container.RegisterSingleton<IQueue<PingRequest>>(() => new RedisQueue<PingRequest>(muxer));

            return container;
        }
    }
}
