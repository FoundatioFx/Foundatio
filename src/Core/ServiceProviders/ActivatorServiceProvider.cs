using System;
using Foundatio.Logging;

namespace Foundatio.ServiceProviders {
    public class ActivatorServiceProvider : IServiceProvider {
        public object GetService(Type serviceType) {
            if (serviceType == null || serviceType.IsInterface || serviceType.IsAbstract)
                return null;

            try {
                return Activator.CreateInstance(serviceType);
            } catch (Exception ex) {
                Logger.Error().Exception(ex).Message("An error occurred while creating instance of type \"{0}\": {1}", serviceType.FullName, ex.Message).Write();
                throw;
            }
        }
    }
}
