using System;

namespace Foundatio.ServiceProviders {
    public interface IBootstrappedServiceProvider : IServiceProvider {
        IServiceProvider ServiceProvider { get; }
        void Bootstrap();
    }
}   