using System;

namespace Foundatio.ServiceProvider {
    public class ActivatorServiceProvider : IServiceProvider {
        public object GetService(Type serviceType) {
            if (serviceType.IsInterface || serviceType.IsAbstract)
                return null;

            try {
                return Activator.CreateInstance(serviceType);
            } catch {
                return null;
            }
        }
    }
}
