using System;

namespace Foundatio.ServiceProviders {
    public class ActivatorServiceProvider : IServiceProvider {
        public object GetService(Type serviceType) {
            if (serviceType == null || serviceType.IsInterface || serviceType.IsAbstract)
                return null;

            try {
                return Activator.CreateInstance(serviceType);
            } catch (Exception ex) {
                throw new ApplicationException($"Error getting service type {serviceType.FullName}: {ex.Message}", ex);
            }
        }
    }
}
