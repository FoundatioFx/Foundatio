using System;
using Foundatio.Caching;
using Foundatio.Messaging;
using Foundatio.AsyncEx;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Diagnostics;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Lock {
    public class CacheLockProvider : ILockProvider {
        private readonly ICacheClient _cacheClient;
        private readonly IMessageBus _messageBus;
        private readonly ConcurrentDictionary<string, AsyncAutoResetEvent> _autoResetEvents = new ConcurrentDictionary<string, AsyncAutoResetEvent>();
        private readonly AsyncLock _lock = new AsyncLock();
        private bool _isSubscribed;
        private readonly ILogger _logger;

        public CacheLockProvider(ICacheClient cacheClient, IMessageBus messageBus, ILoggerFactory loggerFactory = null) {
            _logger = loggerFactory?.CreateLogger<CacheLockProvider>() ?? NullLogger<CacheLockProvider>.Instance;
            _cacheClient = new ScopedCacheClient(cacheClient, "lock");
            _messageBus = messageBus;
        }

        private async Task EnsureTopicSubscriptionAsync() {
            if (_isSubscribed)
                return;

            using (await _lock.LockAsync().AnyContext()) {
                if (_isSubscribed)
                    return;

                bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
                if (isTraceLogLevelEnabled) _logger.LogTrace("Subscribing to cache lock released.");
                await _messageBus.SubscribeAsync<CacheLockReleased>(OnLockReleasedAsync).AnyContext();
                _isSubscribed = true;
                if (isTraceLogLevelEnabled) _logger.LogTrace("Subscribed to cache lock released.");
            }
        }

        private Task OnLockReleasedAsync(CacheLockReleased msg, CancellationToken cancellationToken = default(CancellationToken)) {
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Got lock released message: {Name}", msg.Name);
            if (_autoResetEvents.TryGetValue(msg.Name, out var autoResetEvent))
                autoResetEvent.Set();

            return Task.CompletedTask;
        }

        public async Task<ILock> AcquireAsync(string name, TimeSpan? lockTimeout = null, CancellationToken cancellationToken = default(CancellationToken)) {
            bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
            if (isTraceLogLevelEnabled)
                _logger.LogTrace("AcquireAsync Name: {Name} WillWait: {WillWait}", name, !cancellationToken.IsCancellationRequested);

            if (!cancellationToken.IsCancellationRequested)
                await EnsureTopicSubscriptionAsync().AnyContext();

            if (!lockTimeout.HasValue)
                lockTimeout = TimeSpan.FromMinutes(20);

            bool allowLock = false;

            do {
                bool gotLock = false;

                try {
                    if (lockTimeout.Value == TimeSpan.Zero) // no lock timeout
                        gotLock = await _cacheClient.AddAsync(name, SystemClock.UtcNow).AnyContext();
                    else
                        gotLock = await _cacheClient.AddAsync(name, SystemClock.UtcNow, lockTimeout.Value).AnyContext();
                } catch { }

                if (gotLock) {
                    allowLock = true;
                    if (isTraceLogLevelEnabled) _logger.LogTrace("Acquired lock: {Name}", name);

                    break;
                }

                if (isTraceLogLevelEnabled) _logger.LogTrace("Failed to acquire lock: {Name}", name);
                if (cancellationToken.IsCancellationRequested) {
                    if (isTraceLogLevelEnabled) _logger.LogTrace("Cancellation requested");
                    break;
                }

                var keyExpiration = SystemClock.UtcNow.Add(await _cacheClient.GetExpirationAsync(name).AnyContext() ?? TimeSpan.Zero);
                var delayAmount = keyExpiration.Subtract(SystemClock.UtcNow).Max(TimeSpan.FromMilliseconds(50));

                if (isTraceLogLevelEnabled)
                    _logger.LogTrace("Delay amount: {Delay} Delay until: {DelayUntil}", delayAmount, SystemClock.UtcNow.Add(delayAmount).ToString("mm:ss.fff"));

                var delayCancellationTokenSource = new CancellationTokenSource(delayAmount);
                var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, delayCancellationTokenSource.Token).Token;

                var autoResetEvent = _autoResetEvents.GetOrAdd(name, new AsyncAutoResetEvent());
                var sw = Stopwatch.StartNew();

                try {
                    await autoResetEvent.WaitAsync(linkedCancellationToken).AnyContext();
                } catch (OperationCanceledException) {
                    if (delayCancellationTokenSource.IsCancellationRequested) {
                        if (isTraceLogLevelEnabled)
                            _logger.LogTrace("Retrying: Delay exceeded. Cancellation requested: {IsCancellationRequested}", cancellationToken.IsCancellationRequested);
                        continue;
                    }
                } finally {
                    sw.Stop();
                    if (isTraceLogLevelEnabled)
                        _logger.LogTrace("Lock {Name} waited {Milliseconds}ms", name, sw.ElapsedMilliseconds);
                }
            } while (!cancellationToken.IsCancellationRequested);

            if (cancellationToken.IsCancellationRequested && isTraceLogLevelEnabled)
                _logger.LogTrace("Cancellation requested.");

            if (!allowLock)
                return null;

            if (isTraceLogLevelEnabled)_logger.LogTrace("Returning lock: {Name}", name);
            return new DisposableLock(name, this, _logger);
        }

        public async Task<bool> IsLockedAsync(string name) {
            var result = await Run.WithRetriesAsync(() => _cacheClient.GetAsync<object>(name), logger: _logger).AnyContext();
            return result.HasValue;
        }

        public async Task ReleaseAsync(string name) {
            bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
            if (isTraceLogLevelEnabled) _logger.LogTrace("ReleaseAsync Start: {Name}", name);

            await Run.WithRetriesAsync(() => _cacheClient.RemoveAsync(name), 15, logger: _logger).AnyContext();
            await _messageBus.PublishAsync(new CacheLockReleased { Name = name }).AnyContext();

            if (isTraceLogLevelEnabled) _logger.LogTrace("ReleaseAsync Complete: {Name}", name);
        }

        public Task RenewAsync(string name, TimeSpan? lockExtension = null) {
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("RenewAsync: {Name}", name);
            if (!lockExtension.HasValue)
                lockExtension = TimeSpan.FromMinutes(20);

            return Run.WithRetriesAsync(() => _cacheClient.SetExpirationAsync(name, lockExtension.Value));
        }
    }

    internal class CacheLockReleased {
        public string Name { get; set; }
    }
}