using System;

namespace Foundatio.ServiceProviders {
    public class ActivatorServiceProvider : IServiceProvider {
        public object GetService(Type serviceType) {
            if (serviceType == null || serviceType.IsInterface || serviceType.IsAbstract)
                return null;

            return Activator.CreateInstance(serviceType);
        }
    }
}
