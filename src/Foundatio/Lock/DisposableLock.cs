using System;
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

        public DisposableLock(string name, ILockProvider lockProvider, ILogger logger) {
            _logger = logger;
            _name = name;
            _lockProvider = lockProvider;
        }

        public async Task DisposeAsync() {
            bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
            if (isTraceLogLevelEnabled)
                _logger.LogTrace("Disposing lock: {Name}", _name);

            try {
                await ReleaseAsync().AnyContext();
            } catch (Exception ex) {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(ex, "Unable to release lock {Name}", _name);
            }

            if (isTraceLogLevelEnabled)
                _logger.LogTrace("Disposed lock: {Name}", _name);
        }

        public async Task RenewAsync(TimeSpan? lockExtension = null) {
            bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
            if (isTraceLogLevelEnabled)
                _logger.LogTrace("Renewing lock: {Name}", _name);

            await _lockProvider.RenewAsync(_name, lockExtension).AnyContext();

            if (isTraceLogLevelEnabled)
                _logger.LogTrace("Renewed lock: {Name}", _name);
        }

        public Task ReleaseAsync() {
            if (_isReleased)
                return Task.CompletedTask;

            lock (_lock) {
                if (_isReleased)
                    return Task.CompletedTask;

                _isReleased = true;

                if (_logger.IsEnabled(LogLevel.Trace))
                    _logger.LogTrace("Releasing lock: {Name}", _name);

                return _lockProvider.ReleaseAsync(_name);
            }
        }
    }
}