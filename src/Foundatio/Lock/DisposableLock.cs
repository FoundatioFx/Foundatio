using System;
using System.Threading.Tasks;
using Foundatio.Utility;
using Foundatio.Logging;
using Microsoft.Extensions.Logging;

namespace Foundatio.Lock {
    internal class DisposableLock : ILock {
        private readonly ILockProvider _lockProvider;
        private readonly string _name;
        private readonly ILogger _logger;

        public DisposableLock(string name, ILockProvider lockProvider, ILogger logger) {
            _logger = logger;
            _name = name;
            _lockProvider = lockProvider;
        }

        public async Task DisposeAsync() {
            _logger.LogTrace("Disposing lock: {0}", _name);
            try {
                await _lockProvider.ReleaseAsync(_name).AnyContext();
            } catch (Exception ex) {
                _logger.LogError(ex, $"Unable to release lock {_name}");
            }
            _logger.LogTrace("Disposed lock: {0}", _name);
        }

        public async Task RenewAsync(TimeSpan? lockExtension = null) {
            _logger.LogTrace("Renewing lock: {0}", _name);
            await _lockProvider.RenewAsync(_name, lockExtension).AnyContext();
            _logger.LogTrace("Renewed lock: {0}", _name);
        }

        public Task ReleaseAsync() {
            _logger.LogTrace("Releasing lock: {0}", _name);
            return _lockProvider.ReleaseAsync(_name);
        }
    }
}