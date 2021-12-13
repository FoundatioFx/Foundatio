﻿using System;
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
    public class CacheLockProvider : ILockProvider, IHaveLogger {
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

        ILogger IHaveLogger.Logger => _logger;

        private async Task EnsureTopicSubscriptionAsync() {
            if (_isSubscribed)
                return;

            using (await _lock.LockAsync().AnyContext()) {
                if (_isSubscribed)
                    return;

                bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
                if (isTraceLogLevelEnabled) _logger.LogTrace("Subscribing to cache lock released");
                await _messageBus.SubscribeAsync<CacheLockReleased>(OnLockReleasedAsync).AnyContext();
                _isSubscribed = true;
                if (isTraceLogLevelEnabled) _logger.LogTrace("Subscribed to cache lock released");
            }
        }

        private Task OnLockReleasedAsync(CacheLockReleased msg, CancellationToken cancellationToken = default) {
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("Got lock released message: {Resource} ({LockId})", msg.Resource, msg.LockId);
            
            if (_autoResetEvents.TryGetValue(msg.Resource, out var autoResetEvent))
                autoResetEvent.Target.Set();

            return Task.CompletedTask;
        }

        public async Task<ILock> AcquireAsync(string resource, TimeSpan? timeUntilExpires = null, bool releaseOnDispose = true, CancellationToken cancellationToken = default) {
            bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
            bool shouldWait = !cancellationToken.IsCancellationRequested;
            if (isTraceLogLevelEnabled)
                _logger.LogTrace("Attempting to acquire lock: {Resource}", resource);

            if (!timeUntilExpires.HasValue)
                timeUntilExpires = TimeSpan.FromMinutes(20);

            bool gotLock = false;
            string lockId = GenerateNewLockId();
            var sw = Stopwatch.StartNew();
            try {
                do {
                    try {
                        if (timeUntilExpires.Value == TimeSpan.Zero) // no lock timeout
                            gotLock = await _cacheClient.AddAsync(resource, lockId).AnyContext();
                        else
                            gotLock = await _cacheClient.AddAsync(resource, lockId, timeUntilExpires).AnyContext();
                    } catch { }

                    if (gotLock)
                        break;

                    if (isTraceLogLevelEnabled)
                        _logger.LogTrace("Failed to acquire lock: {Resource}", resource);
                    
                    if (cancellationToken.IsCancellationRequested) {
                        if (isTraceLogLevelEnabled && shouldWait)
                            _logger.LogTrace("Cancellation requested");
                        
                        break;
                    }

                    var autoResetEvent = _autoResetEvents.AddOrUpdate(resource, new ResetEventWithRefCount { RefCount = 1, Target = new AsyncAutoResetEvent() }, (n, e) => { e.RefCount++; return e; });
                    if (!_isSubscribed)
                        await EnsureTopicSubscriptionAsync().AnyContext();

                    var keyExpiration = SystemClock.UtcNow.SafeAdd(await _cacheClient.GetExpirationAsync(resource).AnyContext() ?? TimeSpan.Zero);
                    var delayAmount = keyExpiration.Subtract(SystemClock.UtcNow);
                    
                    // delay a minimum of 50ms
                    if (delayAmount < TimeSpan.FromMilliseconds(50))
                        delayAmount = TimeSpan.FromMilliseconds(50);
                    
                    // delay a maximum of 3 seconds
                    if (delayAmount > TimeSpan.FromSeconds(3))
                        delayAmount = TimeSpan.FromSeconds(3);
                    
                    if (isTraceLogLevelEnabled)
                        _logger.LogTrace("Will wait {Delay:g} before retrying to acquire lock: {Resource}", delayAmount, resource);

                    // wait until we get a message saying the lock was released or 3 seconds has elapsed or cancellation has been requested
                    using (var maxWaitCancellationTokenSource = new CancellationTokenSource(delayAmount))
                    using (var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, maxWaitCancellationTokenSource.Token)) {
                        try {
                            await autoResetEvent.Target.WaitAsync(linkedCancellationTokenSource.Token).AnyContext();
                        } catch (OperationCanceledException) {}
                    }
                    
                    Thread.Yield();
                } while (!cancellationToken.IsCancellationRequested);
            } finally {
                bool shouldRemove = false;
                _autoResetEvents.TryUpdate(resource, (n, e) => {
                    e.RefCount--;
                    if (e.RefCount == 0)
                        shouldRemove = true;
                    return e;
                });

                if (shouldRemove)
                    _autoResetEvents.TryRemove(resource, out var _);
            }
            sw.Stop();

            if (!gotLock) {
                if (cancellationToken.IsCancellationRequested && isTraceLogLevelEnabled)
                    _logger.LogTrace("Cancellation requested for lock {Resource} after {Duration:g}", resource, sw.Elapsed);
                else if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning("Failed to acquire lock {Resource} after {Duration:g}", resource, lockId, sw.Elapsed);
                
                return null;
            }

            if (sw.Elapsed > TimeSpan.FromSeconds(5) && _logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("Acquired lock {Resource} ({LockId}) after {Duration:g}", resource, lockId, sw.Elapsed);
            else if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Acquired lock {Resource} ({LockId}) after {Duration:g}", resource, lockId, sw.Elapsed);
            
            return new DisposableLock(resource, lockId, sw.Elapsed, this, _logger, releaseOnDispose);
        }

        public async Task<bool> IsLockedAsync(string resource) {
            var result = await Run.WithRetriesAsync(() => _cacheClient.ExistsAsync(resource), logger: _logger).AnyContext();
            return result;
        }

        public async Task ReleaseAsync(string resource, string lockId) {
            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("ReleaseAsync Start: {Resource} ({LockId})", resource, lockId);

            await Run.WithRetriesAsync(() => _cacheClient.RemoveIfEqualAsync(resource, lockId), 15, logger: _logger).AnyContext();
            await _messageBus.PublishAsync(new CacheLockReleased { Resource = resource, LockId = lockId }).AnyContext();

            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Released lock: {Resource} ({LockId})", resource, lockId);
        }

        public Task RenewAsync(string resource, string lockId, TimeSpan? timeUntilExpires = null) {
            if (!timeUntilExpires.HasValue)
                timeUntilExpires = TimeSpan.FromMinutes(20);

            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Renewing lock {Resource} ({LockId}) for {Duration:g}", resource, lockId, timeUntilExpires);

            return Run.WithRetriesAsync(() => _cacheClient.ReplaceIfEqualAsync(resource, lockId, lockId, timeUntilExpires.Value));
        }

        private class ResetEventWithRefCount {
            public int RefCount { get; set; }
            public AsyncAutoResetEvent Target { get; set; }
        }

        private static string _allowedChars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        private static Random _rng = new Random();

        private string GenerateNewLockId() {
            char[] chars = new char[16];

            for (int i = 0; i < 16; ++i)
                chars[i] = _allowedChars[_rng.Next(62)];

            return new string(chars, 0, 16);
        }
    }

    public class CacheLockReleased {
        public string Resource { get; set; }
        public string LockId { get; set; }
    }
}
