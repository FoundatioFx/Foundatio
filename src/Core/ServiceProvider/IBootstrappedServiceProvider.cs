using System;

namespace Foundatio.ServiceProvider {
    public interface IBootstrappedServiceProvider : IServiceProvider {
        IServiceProvider Bootstrap();
        object GetService(Type serviceType);
    }
}