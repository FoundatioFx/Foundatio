using System;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Messaging;
using Microsoft.Extensions.Logging;
using SimpleInjector;

namespace Foundatio.SampleJob {
    public class SampleServiceProvider {
        public static IServiceProvider Create(ILoggerFactory loggerFactory) {
            var container = new Container();

            if (loggerFactory != null) {
                container.RegisterSingleton<ILoggerFactory>(loggerFactory);
                container.RegisterSingleton(typeof(ILogger<>), typeof(Logger<>));
            }

            container.RegisterSingleton<ICacheClient>(() => new InMemoryCacheClient(new InMemoryCacheClientOptions()));
            container.RegisterSingleton<ILockProvider>(() => new CacheLockProvider(container.GetInstance<ICacheClient>(), container.GetInstance<IMessageBus>(), loggerFactory));

            return container;
        }
    }
}
