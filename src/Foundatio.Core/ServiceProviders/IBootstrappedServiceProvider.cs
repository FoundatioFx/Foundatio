using System;
using Foundatio.Logging;

namespace Foundatio.ServiceProviders {
    public interface IBootstrappedServiceProvider : IServiceProvider {
        ILoggerFactory LoggerFactory { get; set; }
        IServiceProvider ServiceProvider { get; }
        void Bootstrap();
    }
}   