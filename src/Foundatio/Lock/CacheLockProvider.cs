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

        private Task OnLockReleasedAsync(CacheLockReleased msg, CancellationToken cancellationToken = default) {
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Got lock released message: {Name}", msg.Name);
            if (_autoResetEvents.TryGetValue(msg.Name, out var autoResetEvent))
                autoResetEvent.Set();

            return Task.CompletedTask;
        }

        public async Task<ILock> AcquireAsync(string name, TimeSpan? lockTimeout = null, CancellationToken cancellationToken = default) {
            bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
            if (isTraceLogLevelEnabled)
                _logger.LogTrace("AcquireAsync Name: {Name} WillWait: {WillWait}", name, !cancellationToken.IsCancellationRequested);

            if (!cancellationToken.IsCancellationRequested)
                await EnsureTopicSubscriptionAsync().AnyContext();

            if (!lockTimeout.HasValue)
                lockTimeout = TimeSpan.FromMinutes(20);

            bool gotLock = false;
            var sw = Stopwatch.StartNew();

            do {
                try {
                    if (lockTimeout.Value == TimeSpan.Zero) // no lock timeout
                        gotLock = await _cacheClient.AddAsync(name, SystemClock.UtcNow).AnyContext();
                    else
                        gotLock = await _cacheClient.AddAsync(name, SystemClock.UtcNow, lockTimeout.Value).AnyContext();
                } catch { }

                if (gotLock)
                    break;

                if (isTraceLogLevelEnabled)
                    _logger.LogTrace("Failed to acquire lock: {Name}", name);
                
                if (cancellationToken.IsCancellationRequested) {
                    if (isTraceLogLevelEnabled)
                        _logger.LogTrace("Cancellation requested");
                    
                    break;
                }

                var keyExpiration = SystemClock.UtcNow.SafeAdd(await _cacheClient.GetExpirationAsync(name).AnyContext() ?? TimeSpan.Zero);
                var delayAmount = keyExpiration.Subtract(SystemClock.UtcNow).Max(TimeSpan.FromMilliseconds(50));

                if (isTraceLogLevelEnabled)
                    _logger.LogTrace("Delay amount: {Delay} Delay until: {DelayUntil}", delayAmount, SystemClock.UtcNow.SafeAdd(delayAmount).ToString("mm:ss.fff"));

                using (var delayCancellationTokenSource = new CancellationTokenSource(delayAmount))
                using (var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, delayCancellationTokenSource.Token)) {
                    var autoResetEvent = _autoResetEvents.GetOrAdd(name, new AsyncAutoResetEvent());

                    try {
                        await autoResetEvent.WaitAsync(linkedCancellationTokenSource.Token).AnyContext();
                    } catch (OperationCanceledException) {
                        if (delayCancellationTokenSource.IsCancellationRequested) {
                            if (isTraceLogLevelEnabled)
                                _logger.LogTrace("Retrying: Delay exceeded. Cancellation requested: {IsCancellationRequested}", cancellationToken.IsCancellationRequested);
                            continue;
                        }
                    }
                }
            } while (!cancellationToken.IsCancellationRequested);
            sw.Stop();

            if (cancellationToken.IsCancellationRequested && isTraceLogLevelEnabled)
                _logger.LogTrace("Cancellation requested.");

            if (!gotLock) {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("Failed to acquire lock {Name} after {Duration:g}", name, sw.Elapsed);
                
                return null;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Acquired lock {Name} after {Duration:g}", name, sw.Elapsed);
            
            return new DisposableLock(name, this, _logger);
        }

        public async Task<bool> IsLockedAsync(string name) {
            var result = await Run.WithRetriesAsync(() => _cacheClient.GetAsync<object>(name), logger: _logger).AnyContext();
            return result.HasValue;
        }

        public async Task ReleaseAsync(string name) {
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("ReleaseAsync Start {Name}", name);

            await Run.WithRetriesAsync(() => _cacheClient.RemoveAsync(name), 15, logger: _logger).AnyContext();
            await _messageBus.PublishAsync(new CacheLockReleased { Name = name }).AnyContext();

            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Released lock {Name}", name);
        }

        public Task RenewAsync(string name, TimeSpan? lockExtension = null) {
            if (!lockExtension.HasValue)
                lockExtension = TimeSpan.FromMinutes(20);

            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Renewing lock {Name} for {Duration:g}", name, lockExtension);

            return Run.WithRetriesAsync(() => _cacheClient.SetExpirationAsync(name, lockExtension.Value));
        }
    }

    public class CacheLockReleased {
        public string Name { get; set; }
    }
}