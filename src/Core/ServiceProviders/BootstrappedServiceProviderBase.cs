using System;

namespace Foundatio.ServiceProviders {
    public abstract class BootstrappedServiceProviderBase : IBootstrappedServiceProvider {
        private static IServiceProvider _serviceProvider;
        
        public abstract IServiceProvider Bootstrap();

        public object GetService(Type serviceType) {
            if (_serviceProvider == null)
                _serviceProvider = Bootstrap();

            return _serviceProvider.GetService(serviceType);
        }
    }
}