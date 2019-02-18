using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Hosting.Startup {
    public class StartupContext {
        private readonly ILogger _logger;
        private int _waitCount = 0;

        public StartupContext(ILogger<StartupContext> logger) {
            _logger = logger;
        }

        public bool IsStartupComplete { get; private set; }
        public bool StartupActionsFailed { get; private set; }

        internal void MarkStartupComplete() {
            IsStartupComplete = true;
        }

        internal void MarkStartupFailure() {
            StartupActionsFailed = true;
        }

        public async Task<bool> WaitForStartupAsync(CancellationToken cancellationToken) {
            bool isFirstWaiter = Interlocked.Increment(ref _waitCount) == 1;
            var startTime = SystemClock.UtcNow;
            var lastStatus = SystemClock.UtcNow;

            while (!cancellationToken.IsCancellationRequested) {
                if (IsStartupComplete)
                    return true;

                if (StartupActionsFailed)
                    return false;

                if (isFirstWaiter && SystemClock.UtcNow.Subtract(lastStatus) > TimeSpan.FromSeconds(5) && _logger.IsEnabled(LogLevel.Information)) {
                    lastStatus = SystemClock.UtcNow;
                    _logger.LogInformation("Waiting for startup actions to be completed for {Duration:mm\\:ss}...", SystemClock.UtcNow.Subtract(startTime));
                }

                await Task.Delay(1000, cancellationToken).AnyContext();
            }

            if (isFirstWaiter && _logger.IsEnabled(LogLevel.Error))
                _logger.LogError("Timed out waiting for startup actions to be completed after {Duration:mm\\:ss}", SystemClock.UtcNow.Subtract(startTime));

            return false;
        }
    }
}