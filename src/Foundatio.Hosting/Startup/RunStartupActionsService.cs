using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Startup;
using Microsoft.Extensions.Hosting;

namespace Foundatio.Hosting.Startup {
    public class RunStartupActionsService : BackgroundService {
        private readonly StartupContext _startupContext;
        private readonly IServiceProvider _serviceProvider;

        public RunStartupActionsService(StartupContext startupContext, IServiceProvider serviceProvider) {
            _startupContext = startupContext;
            _serviceProvider = serviceProvider;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
            await _serviceProvider.RunStartupActionsAsync(stoppingToken).ConfigureAwait(false);
            _startupContext.MarkStartupComplete();
        }
    }
}