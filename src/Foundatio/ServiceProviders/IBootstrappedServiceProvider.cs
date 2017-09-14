using System;
using Foundatio.Logging;
using Microsoft.Extensions.Logging;

namespace Foundatio.ServiceProviders {
    public interface IBootstrappedServiceProvider : IServiceProvider {
        ILoggerFactory LoggerFactory { get; set; }
        IServiceProvider ServiceProvider { get; }
        void Bootstrap();
    }
}   