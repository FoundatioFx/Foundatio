using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Lock {
    internal class DisposableLock : ILock {
        private readonly ILockProvider _lockProvider;
        private readonly string _name;
        private readonly ILogger _logger;
        private bool _isReleased;
        private readonly object _lock = new object();
        private readonly Stopwatch _duration;

        public DisposableLock(string name, ILockProvider lockProvider, ILogger logger) {
            _duration = Stopwatch.StartNew();
            _logger = logger;
            _name = name;
            _lockProvider = lockProvider;
        }

        public async Task DisposeAsync() {
            bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
            if (isTraceLogLevelEnabled)
                _logger.LogTrace("Disposing lock {Name}", _name);

            try {
                await ReleaseAsync().AnyContext();
            } catch (Exception ex) {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(ex, "Unable to release lock {Name}", _name);
            }

            if (isTraceLogLevelEnabled)
                _logger.LogTrace("Disposed lock {Name}", _name);
        }

        public async Task RenewAsync(TimeSpan? lockExtension = null) {
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("Renewing lock {Name}", _name);

            await _lockProvider.RenewAsync(_name, lockExtension).AnyContext();

            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Renewed lock {Name}", _name);
        }

        public Task ReleaseAsync() {
            if (_isReleased)
                return Task.CompletedTask;

            lock (_lock) {
                if (_isReleased)
                    return Task.CompletedTask;

                _isReleased = true;
                _duration.Stop();

                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("Releasing lock {Name} after {Duration:g}", _name, _duration.Elapsed);

                return _lockProvider.ReleaseAsync(_name);
            }
        }
    }
}