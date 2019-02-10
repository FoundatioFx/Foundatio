using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Hosting.Startup {
    public class StartupContext {
        private readonly ILogger _logger;

        public StartupContext(ILogger<StartupContext> logger) {
            _logger = logger;
        }

        public bool IsStartupComplete { get; private set; }

        internal void MarkStartupComplete() {
            IsStartupComplete = true;
        }
        
        public async Task<bool> WaitForStartupAsync(CancellationToken cancellationToken) {
            var startTime = SystemClock.UtcNow;

            while (!cancellationToken.IsCancellationRequested) {
                if (IsStartupComplete)
                    return true;

                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("Waiting for startup actions to be completed for {Duration:g}...", SystemClock.UtcNow.Subtract(startTime));

                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }

            if (_logger.IsEnabled(LogLevel.Error))
                _logger.LogError("Timed out waiting for startup actions to be completed after {Duration:g}", SystemClock.UtcNow.Subtract(startTime));

            return false;
        }
    }
}