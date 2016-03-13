using System;
using Foundatio.Logging;

namespace Foundatio.ServiceProviders {
    public abstract class BootstrappedServiceProviderBase : IBootstrappedServiceProvider {
        public BootstrappedServiceProviderBase() {}

        public BootstrappedServiceProviderBase(ILoggerFactory loggerFactory) {
            LoggerFactory = loggerFactory;
        }

        public ILoggerFactory LoggerFactory { get; set; }
        public IServiceProvider ServiceProvider { get; private set; }

        public void Bootstrap() {
            lock (_lock) {
                if (ServiceProvider == null)
                    ServiceProvider = BootstrapInternal(LoggerFactory);
            }
        }

        protected abstract IServiceProvider BootstrapInternal(ILoggerFactory loggerFactory);

        private readonly object _lock = new object();

        public object GetService(Type serviceType) {
            if (ServiceProvider != null)
                return ServiceProvider.GetService(serviceType);

            Bootstrap();

            return ServiceProvider?.GetService(serviceType);
        }
    }
}