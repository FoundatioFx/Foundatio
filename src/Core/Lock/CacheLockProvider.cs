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
using Foundatio.Utility;

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
                _logger.Trace("Subscribing to cache lock released.");
                _messageBus.Subscribe<CacheLockReleased>(OnLockReleasedAsync);
            }
        }

        private async Task OnLockReleasedAsync(CacheLockReleased msg, CancellationToken cancellationToken = default(CancellationToken)) {
            _logger.Trace("Got lock released message: {Name}", msg.Name);

            AsyncMonitor monitor;
            if (!_monitors.TryGetValue(msg.Name, out monitor))
                return;

            using (await monitor.EnterAsync())
                monitor.Pulse();
        }

        public async Task<ILock> AcquireAsync(string name, TimeSpan? lockTimeout = null, CancellationToken cancellationToken = default(CancellationToken)) {
            _logger.Trace(() => $"AcquireAsync Name: {name} WillWait: {!cancellationToken.IsCancellationRequested}");

            if (!cancellationToken.IsCancellationRequested)
                EnsureTopicSubscription();

            if (!lockTimeout.HasValue)
                lockTimeout = TimeSpan.FromMinutes(20);
            
            bool allowLock = false;

            do {
                bool gotLock;
                if (lockTimeout.Value == TimeSpan.Zero) // no lock timeout
                    gotLock = await Run.WithRetriesAsync(() => _cacheClient.AddAsync(name, DateTime.UtcNow), cancellationToken: cancellationToken).AnyContext();
                else
                    gotLock = await Run.WithRetriesAsync(() => _cacheClient.AddAsync(name, DateTime.UtcNow, lockTimeout.Value), cancellationToken: cancellationToken).AnyContext();

                if (gotLock) {
                    allowLock = true;
                    _logger.Trace("Acquired lock: {name}", name);

                    break;
                }

                _logger.Trace("Failed to acquire lock: {name}", name);
                if (cancellationToken.IsCancellationRequested) {
                    _logger.Trace("Cancellation requested");
                    break;
                }

                var keyExpiration = DateTime.UtcNow.Add(await _cacheClient.GetExpirationAsync(name).AnyContext() ?? TimeSpan.Zero);
                var delayAmount = keyExpiration.Subtract(DateTime.UtcNow).Max(TimeSpan.FromMilliseconds(50));

                _logger.Trace("Delay amount: {0} Delay until: {1}", delayAmount, DateTime.UtcNow.Add(delayAmount).ToString("mm:ss.fff"));

                var delayCancellationTokenSource = new CancellationTokenSource(delayAmount);
                var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, delayCancellationTokenSource.Token).Token;

                var monitor = _monitors.GetOrAdd(name, new AsyncMonitor());
                var sw = Stopwatch.StartNew();

                try {
                    using (await monitor.EnterAsync(linkedCancellationToken))
                        await monitor.WaitAsync(linkedCancellationToken).AnyContext();
                } catch (TaskCanceledException) {
                    if (delayCancellationTokenSource.IsCancellationRequested) {
                        _logger.Trace("Retrying: Delay exceeded");
                        continue;
                    }
                } finally {
                    sw.Stop();
                    _logger.Trace("Lock {name} waited {milliseconds}ms", name, sw.ElapsedMilliseconds);
                }
            } while (!cancellationToken.IsCancellationRequested);

            if (cancellationToken.IsCancellationRequested)
                _logger.Trace("Cancellation requested.");

            if (!allowLock)
                return null;

            _logger.Trace("Returning lock: {name}", name);

            return new DisposableLock(name, this, _logger);
        }

        public async Task<bool> IsLockedAsync(string name) {
            var result = await Run.WithRetriesAsync(() => _cacheClient.GetAsync<object>(name)).AnyContext();
            return result.HasValue;
        }

        public async Task ReleaseAsync(string name) {
            _logger.Trace("ReleaseAsync: {name}", name);

            await Run.WithRetriesAsync(() => _cacheClient.RemoveAsync(name), 15).AnyContext();
            await _messageBus.PublishAsync(new CacheLockReleased { Name = name }).AnyContext();
        }

        public async Task RenewAsync(String name, TimeSpan? lockExtension = null) {
            _logger.Trace("RenewAsync: {0}", name);
            if (!lockExtension.HasValue)
                lockExtension = TimeSpan.FromMinutes(20);

            await Run.WithRetriesAsync(() => _cacheClient.SetExpirationAsync(name, lockExtension.Value)).AnyContext();
        }

        public void Dispose() { }
    }

    internal class CacheLockReleased {
        public string Name { get; set; }
    }
}