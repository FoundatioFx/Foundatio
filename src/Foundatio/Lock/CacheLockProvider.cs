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
        private readonly ConcurrentDictionary<string, ResetEventWithRefCount> _autoResetEvents = new ConcurrentDictionary<string, ResetEventWithRefCount>();
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
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("Got lock released message: {Name}", msg.Name);
            
            if (_autoResetEvents.TryGetValue(msg.Name, out var autoResetEvent))
                autoResetEvent.Target.Set();

            return Task.CompletedTask;
        }

        public async Task<ILock> AcquireAsync(string name, TimeSpan? lockTimeout = null, CancellationToken cancellationToken = default) {
            bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
            bool shouldWait = !cancellationToken.IsCancellationRequested;
            if (isTraceLogLevelEnabled)
                _logger.LogTrace("AcquireAsync Name: {Name} ShouldWait: {ShouldWait}", name, shouldWait);

            if (!lockTimeout.HasValue)
                lockTimeout = TimeSpan.FromMinutes(20);

            bool gotLock = false;
            var sw = Stopwatch.StartNew();
            try {
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
                        if (isTraceLogLevelEnabled && shouldWait)
                            _logger.LogTrace("Cancellation requested");
                        
                        break;
                    }

                    var autoResetEvent = _autoResetEvents.AddOrUpdate(name, new ResetEventWithRefCount { RefCount = 1, Target = new AsyncAutoResetEvent() }, (n, e) => { e.RefCount++; return e; });
                    if (!_isSubscribed)
                        await EnsureTopicSubscriptionAsync().AnyContext();

                    var keyExpiration = SystemClock.UtcNow.SafeAdd(await _cacheClient.GetExpirationAsync(name).AnyContext() ?? TimeSpan.Zero);
                    var delayAmount = keyExpiration.Subtract(SystemClock.UtcNow).Max(TimeSpan.FromMilliseconds(50)).Min(TimeSpan.FromSeconds(3));
                    
                    if (isTraceLogLevelEnabled)
                        _logger.LogTrace("Will wait {Delay} before retrying to acquire cache lock {Name}.", delayAmount, name);

                    // wait until we get a message saying the lock was released or 3 seconds has elapsed or cancellation has been requested
                    using (var maxWaitCancellationTokenSource = new CancellationTokenSource(delayAmount))
                    using (var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, maxWaitCancellationTokenSource.Token)) {
                        try {
                            await autoResetEvent.Target.WaitAsync(linkedCancellationTokenSource.Token).AnyContext();
                        } catch (OperationCanceledException) {
                            if (maxWaitCancellationTokenSource.IsCancellationRequested)
                                continue;
                        }
                    }
                } while (!cancellationToken.IsCancellationRequested);
            } finally {
                bool shouldRemove = false;
                _autoResetEvents.TryUpdate(name, (n, e) => {
                    e.RefCount--;
                    if (e.RefCount == 0)
                        shouldRemove = true;
                    return e;
                });

                if (shouldRemove)
                    _autoResetEvents.TryRemove(name, out var _);
            }
            sw.Stop();

            if (!gotLock) {
                if (cancellationToken.IsCancellationRequested && isTraceLogLevelEnabled)
                    _logger.LogTrace("Cancellation requested for lock {Name} after {Duration:g}", name, sw.Elapsed);
                else if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("Failed to acquire lock {Name} after {Duration:g}", name, sw.Elapsed);
                
                return null;
            }

            if (sw.Elapsed > TimeSpan.FromSeconds(5) && _logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("Acquired lock {Name} after {Duration:g}", name, sw.Elapsed);
            else if (_logger.IsEnabled(LogLevel.Debug))
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

        private class ResetEventWithRefCount {
            public int RefCount { get; set; }
            public AsyncAutoResetEvent Target { get; set; }
        }
    }

    public class CacheLockReleased {
        public string Name { get; set; }
    }
}