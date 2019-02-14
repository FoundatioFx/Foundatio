using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Startup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Foundatio.Hosting.Startup {
    public class RunStartupActionsService : BackgroundService {
        private readonly StartupContext _startupContext;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger _logger;

        public RunStartupActionsService(StartupContext startupContext, IServiceProvider serviceProvider, ILogger<RunStartupActionsService> logger) {
            _startupContext = startupContext;
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
            _logger.LogInformation("Running startup actions...");
            await _serviceProvider.RunStartupActionsAsync(stoppingToken).ConfigureAwait(false);
            _logger.LogInformation("Startup actions complete.");
            _startupContext.MarkStartupComplete();
        }
    }
}