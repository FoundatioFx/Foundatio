using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Hosting;

namespace Foundatio.Hosting.Startup {
    public class RunStartupActionsService : BackgroundService {
        private readonly StartupActionsContext _startupContext;
        private readonly IServiceProvider _serviceProvider;

        public RunStartupActionsService(StartupActionsContext startupContext, IServiceProvider serviceProvider) {
            _startupContext = startupContext;
            _serviceProvider = serviceProvider;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
            var result = await _serviceProvider.RunStartupActionsAsync(stoppingToken).AnyContext();
            _startupContext.MarkStartupComplete(result);
        }
    }
}