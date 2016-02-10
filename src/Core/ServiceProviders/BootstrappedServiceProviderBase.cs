using System;
using Foundatio.Logging;

namespace Foundatio.ServiceProviders {
    public abstract class BootstrappedServiceProviderBase : IBootstrappedServiceProvider {
        private static IServiceProvider _serviceProvider;
        
        public abstract IServiceProvider Bootstrap();

        private static readonly object _lock = new object();

        public object GetService(Type serviceType) {
            if (_serviceProvider == null) {
                lock (_lock) {
                    if (_serviceProvider == null)
                        _serviceProvider = Bootstrap();
                }
            }

            try {
                return _serviceProvider.GetService(serviceType);
            } catch (Exception ex) {
                Logger.Error().Exception(ex).Message("Error getting service: " + ex.Message).Write();
                throw;
            }
        }
    }
}