using System;
using System.Reflection;

namespace Foundatio.ServiceProviders {
    public class ActivatorServiceProvider : IServiceProvider {
        public object GetService(Type serviceType) {
            if (serviceType == null)
                return null;

            var typeInfo = serviceType.GetTypeInfo();
            if (typeInfo.IsInterface || typeInfo.IsAbstract)
                return null;

            try {
                return Activator.CreateInstance(serviceType);
            } catch (Exception ex) {
                throw new Exception($"Error getting service type {serviceType.FullName}: {ex.Message}", ex);
            }
        }
    }
}
