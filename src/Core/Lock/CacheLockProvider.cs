using System;
using Foundatio.Caching;
using Foundatio.Logging;
using Foundatio.Messaging;
using Nito.AsyncEx;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Diagnostics;
using Foundatio.Extensions;

namespace Foundatio.Lock {
    public class CacheLockProvider : ILockProvider {
        private readonly ICacheClient _cacheClient;
        private readonly IMessageBus _messageBus;
        private readonly ConcurrentDictionary<string, AsyncMonitor> _monitors = new ConcurrentDictionary<string, AsyncMonitor>();
        private static readonly object _lockObject = new object();
        private bool _isSubscribed;
        protected readonly ILogger _logger;

        public CacheLockProvider(ICacheClient cacheClient, IMessageBus messageBus, ILoggerFactory loggerFactory = null) {
            _logger = loggerFactory?.CreateLogger<CacheLockProvider>() ?? NullLogger.Instance;
            _cacheClient = new ScopedCacheClient(cacheClient, "lock");
            _messageBus = messageBus;
        }

        private void EnsureTopicSubscription() {
            if (_isSubscribed)
                return;

            lock (_lockObject) {
                if (_isSubscribed)
                    return;

                _isSubscribed = true;
                _logger.Trace().Message("Subscribing to cache lock released.").Write();
                _messageBus.Subscribe<CacheLockReleased>(OnLockReleasedAsync);
            }
        }

        private async Task OnLockReleasedAsync(CacheLockReleased msg, CancellationToken cancellationToken = default(CancellationToken)) {
            _logger.Trace().Message($"Got lock released message: {msg.Name}").Write();

            AsyncMonitor monitor;
            if (!_monitors.TryGetValue(msg.Name, out monitor))
                return;

            using (await monitor.EnterAsync())
                monitor.Pulse();
        }

        public async Task<ILock> AcquireAsync(string name, TimeSpan? lockTimeout = null, CancellationToken cancellationToken = default(CancellationToken)) {
            _logger.Trace().Message($"AcquireAsync: {name}").Write();

            EnsureTopicSubscription();
            if (!lockTimeout.HasValue)
                lockTimeout = TimeSpan.FromMinutes(20);
            
            bool allowLock = false;

            do {
                bool gotLock;
                if (lockTimeout.Value == TimeSpan.Zero) // no lock timeout
                    gotLock = await _cacheClient.AddAsync(name, DateTime.UtcNow).AnyContext();
                else
                    gotLock = await _cacheClient.AddAsync(name, DateTime.UtcNow, lockTimeout.Value).AnyContext();

                if (gotLock) {
                    allowLock = true;
                    _logger.Trace().Message($"Acquired lock: {name}").Write();

                    break;
                }

                _logger.Trace().Message($"Failed to acquire lock: {name}").Write();
                if (cancellationToken.IsCancellationRequested) {
                    _logger.Trace().Message("Cancellation Requested").Write();
                    break;
                }

                var keyExpiration = DateTime.UtcNow.Add(await _cacheClient.GetExpirationAsync(name).AnyContext() ?? TimeSpan.Zero);
                var delayAmount = keyExpiration.Subtract(DateTime.UtcNow).Max(TimeSpan.FromMilliseconds(50));

                _logger.Trace().Message("Delay amount: {0} Delay until: {1}", delayAmount, DateTime.UtcNow.Add(delayAmount).ToString("mm:ss.fff")).Write();

                var delayCancellationTokenSource = new CancellationTokenSource(delayAmount);
                var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, delayCancellationTokenSource.Token).Token;

                var monitor = _monitors.GetOrAdd(name, new AsyncMonitor());
                var sw = Stopwatch.StartNew();

                try {
                    using (await monitor.EnterAsync(linkedCancellationToken))
                        await monitor.WaitAsync(linkedCancellationToken).AnyContext();
                } catch (TaskCanceledException) {
                    if (delayCancellationTokenSource.IsCancellationRequested) {
                        _logger.Trace().Message("Retrying: Delay exceeded").Write();
                        continue;
                    }
                } finally {
                    sw.Stop();
                    _logger.Trace().Message($"Lock {name} waited {sw.ElapsedMilliseconds}ms").Write();
                }
            } while (!cancellationToken.IsCancellationRequested);

            if (cancellationToken.IsCancellationRequested)
                _logger.Trace().Message("Cancellation requested.").Write();

            if (!allowLock)
                return null;

            _logger.Trace().Message($"Returning lock: {name}").Write();

            return new DisposableLock(name, this, _logger);
        }

        public async Task<bool> IsLockedAsync(string name) {
            return (await _cacheClient.GetAsync<object>(name).AnyContext()).HasValue;
        }

        public async Task ReleaseAsync(string name) {
            _logger.Trace().Message($"ReleaseAsync: {name}").Write();

            await _cacheClient.RemoveAsync(name).AnyContext();
            await _messageBus.PublishAsync(new CacheLockReleased { Name = name }).AnyContext();
        }

        public async Task RenewAsync(String name, TimeSpan? lockExtension = null) {
            _logger.Trace().Message("RenewAsync: {0}", name).Write();
            if (!lockExtension.HasValue)
                lockExtension = TimeSpan.FromMinutes(20);

            await _cacheClient.SetExpirationAsync(name, lockExtension.Value).AnyContext();
        }

        public void Dispose() { }
    }

    internal class CacheLockReleased {
        public string Name { get; set; }
    }
}