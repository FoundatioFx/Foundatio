using System;

namespace Foundatio.ServiceProviders {
    public abstract class BootstrappedServiceProviderBase : IBootstrappedServiceProvider {
        public IServiceProvider ServiceProvider { get; private set; }

        public void Bootstrap() {
            lock (_lock) {
                if (ServiceProvider == null)
                    ServiceProvider = BootstrapInternal();
            }
        }

        protected abstract IServiceProvider BootstrapInternal();

        private readonly object _lock = new object();

        public object GetService(Type serviceType) {
            if (ServiceProvider != null)
                return ServiceProvider.GetService(serviceType);

            Bootstrap();

            return ServiceProvider?.GetService(serviceType);
        }
    }
}