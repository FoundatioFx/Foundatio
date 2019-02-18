using System;
using System.Threading;
using System.Threading.Tasks;
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
            var success = await _serviceProvider.RunStartupActionsAsync(stoppingToken).ConfigureAwait(false);
            if (success)
                _startupContext.MarkStartupComplete();
            else
                _startupContext.MarkStartupFailure();
        }
    }
}