using System;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Logging;
using Foundatio.Messaging;
using Foundatio.ServiceProviders;
using SimpleInjector;

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

            container.RegisterSingleton<ICacheClient>(() => new InMemoryCacheClient());
            container.RegisterSingleton<ILockProvider>(() => new CacheLockProvider(container.GetInstance<ICacheClient>(), container.GetInstance<IMessageBus>(), loggerFactory));

            return container;
        }
    }
}
