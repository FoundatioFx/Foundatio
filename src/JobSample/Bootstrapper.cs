using System;
using System.Threading.Tasks;
using Exceptionless;
using Foundatio.Extensions;
using Foundatio.Queues;
using Foundatio.ServiceProviders;
using SimpleInjector;
using StackExchange.Redis;

namespace Foundatio.JobSample.Jobs {
    public class FoundatioBootstrapper : BootstrappedServiceProviderBase {
        public override IServiceProvider Bootstrap() {
            var container = new Container();
            container.Options.AllowOverridingRegistrations = true;

            container.Register<IAmADependency, MyDependency>();
            var muxer = ConnectionMultiplexer.Connect("localhost");
            container.RegisterSingleton(muxer);

            var q1 = new RedisQueue<PingRequest>(muxer);
            var q2 = new RedisQueue<PingRequest>(muxer);
            container.RegisterSingleton<IQueue<PingRequest>>(() => q2);

            Task.Run(async () => {
                var startDate = DateTime.Now;
                while (startDate.AddSeconds(30) > DateTime.Now) {
                    Console.WriteLine("Enqueueing ping.");
                    await q1.EnqueueAsync(new PingRequest { Data = "Hi" }).AnyContext();
                    await Task.Delay(RandomData.GetInt(100, 1000)).AnyContext();
                }
            }).AnyContext();

            return container;
        }
    }
}
