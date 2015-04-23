using System;

namespace Foundatio.ServiceProviders {
    public interface IBootstrappedServiceProvider : IServiceProvider {
        IServiceProvider Bootstrap();
    }
}