using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Caching;
using Foundatio.Messaging;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Lock;

public class CacheLockProvider : ILockProvider, IHaveLogger, IHaveTimeProvider
{
    private readonly ICacheClient _cacheClient;
    private readonly IMessageBus _messageBus;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, ResetEventWithRefCount> _autoResetEvents = new();
    private readonly AsyncLock _lock = new();
    private bool _isSubscribed;
    private readonly ILogger _logger;
    private readonly Histogram<double> _lockWaitTimeHistogram;
    private readonly Counter<int> _lockTimeoutCounter;

    public CacheLockProvider(ICacheClient cacheClient, IMessageBus messageBus, ILoggerFactory loggerFactory = null) : this(cacheClient, messageBus, null, loggerFactory) { }

    public CacheLockProvider(ICacheClient cacheClient, IMessageBus messageBus, TimeProvider timeProvider, ILoggerFactory loggerFactory = null)
    {
        _timeProvider = timeProvider ?? cacheClient.GetTimeProvider();
        _logger = loggerFactory?.CreateLogger<CacheLockProvider>() ?? NullLogger<CacheLockProvider>.Instance;
        _cacheClient = new ScopedCacheClient(cacheClient, "lock");
        _messageBus = messageBus;

        _lockWaitTimeHistogram = FoundatioDiagnostics.Meter.CreateHistogram<double>("foundatio.lock.wait.time", description: "Time waiting for locks", unit: "ms");
        _lockTimeoutCounter = FoundatioDiagnostics.Meter.CreateCounter<int>("foundatio.lock.failed", description: "Number of failed attempts to acquire a lock");
    }

    ILogger IHaveLogger.Logger => _logger;
    TimeProvider IHaveTimeProvider.TimeProvider => _timeProvider;

    private async Task EnsureTopicSubscriptionAsync()
    {
        if (_isSubscribed)
            return;

        using (await _lock.LockAsync().AnyContext())
        {
            if (_isSubscribed)
                return;

            bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
            if (isTraceLogLevelEnabled) _logger.LogTrace("Subscribing to cache lock released");
            await _messageBus.SubscribeAsync<CacheLockReleased>(OnLockReleasedAsync).AnyContext();
            _isSubscribed = true;
            if (isTraceLogLevelEnabled) _logger.LogTrace("Subscribed to cache lock released");
        }
    }

    private Task OnLockReleasedAsync(CacheLockReleased msg, CancellationToken cancellationToken = default)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Got lock released message: {Resource} ({LockId})", msg.Resource, msg.LockId);

        if (_autoResetEvents.TryGetValue(msg.Resource, out var autoResetEvent))
            autoResetEvent.Target.Set();

        return Task.CompletedTask;
    }

    protected virtual Activity StartLockActivity(string resource)
    {
        var activity = FoundatioDiagnostics.ActivitySource.StartActivity("AcquireLock");
        if (activity is null)
            return null;

        activity.AddTag("resource", resource);
        activity.DisplayName = $"Lock: {resource}";

        return activity;
    }

    public async Task<ILock> AcquireAsync(string resource, TimeSpan? timeUntilExpires = null, bool releaseOnDispose = true, CancellationToken cancellationToken = default)
    {
        bool isTraceLogLevelEnabled = _logger.IsEnabled(LogLevel.Trace);
        bool isDebugLogLevelEnabled = _logger.IsEnabled(LogLevel.Debug);
        bool shouldWait = !cancellationToken.IsCancellationRequested;
        string lockId = GenerateNewLockId();
        timeUntilExpires ??= TimeSpan.FromMinutes(20);

        if (isDebugLogLevelEnabled)
            _logger.LogDebug("Attempting to acquire lock {Resource} ({LockId})", resource, lockId);

        using var activity = StartLockActivity(resource);

        bool gotLock = false;
        var sw = Stopwatch.StartNew();
        try
        {
            do
            {
                try
                {
                    if (timeUntilExpires.Value == TimeSpan.Zero) // no lock timeout
                        gotLock = await _cacheClient.AddAsync(resource, lockId).AnyContext();
                    else
                        gotLock = await _cacheClient.AddAsync(resource, lockId, timeUntilExpires).AnyContext();
                }
                catch (Exception ex)
                {
                    if (isTraceLogLevelEnabled)
                        _logger.LogTrace(ex, "Error acquiring lock {Resource} ({LockId})", resource, lockId);
                }

                if (gotLock)
                    break;

                if (isDebugLogLevelEnabled)
                    _logger.LogDebug("Failed to acquire lock {Resource} ({LockId})", resource, lockId);

                if (cancellationToken.IsCancellationRequested)
                {
                    if (isTraceLogLevelEnabled && shouldWait)
                        _logger.LogTrace("Cancellation requested while acquiring lock {Resource} ({LockId})", resource, lockId);

                    break;
                }

                var autoResetEvent = _autoResetEvents.AddOrUpdate(resource, new ResetEventWithRefCount { RefCount = 1, Target = new AsyncAutoResetEvent() }, (n, e) => { e.RefCount++; return e; });
                if (!_isSubscribed)
                    await EnsureTopicSubscriptionAsync().AnyContext();

                var keyExpiration = _timeProvider.GetUtcNow().UtcDateTime.SafeAdd(await _cacheClient.GetExpirationAsync(resource).AnyContext() ?? TimeSpan.Zero);
                var delayAmount = keyExpiration.Subtract(_timeProvider.GetUtcNow().UtcDateTime);

                // delay a minimum of 50ms and a maximum of 3 seconds
                if (delayAmount < TimeSpan.FromMilliseconds(50))
                    delayAmount = TimeSpan.FromMilliseconds(50);
                else if (delayAmount > TimeSpan.FromSeconds(3))
                    delayAmount = TimeSpan.FromSeconds(3);

                if (isTraceLogLevelEnabled)
                    _logger.LogTrace("Will wait {Delay:g} before retrying to acquire lock {Resource} ({LockId})", delayAmount, resource, lockId);

                // wait until we get a message saying the lock was released or 3 seconds has elapsed or cancellation has been requested
                using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linkedCancellationTokenSource.CancelAfter(delayAmount);

                try
                {
                    await autoResetEvent.Target.WaitAsync(linkedCancellationTokenSource.Token).AnyContext();
                }
                catch (OperationCanceledException) { }

                Thread.Yield();
            } while (!cancellationToken.IsCancellationRequested);
        }
        finally
        {
            bool shouldRemove = false;
            _autoResetEvents.TryUpdate(resource, (n, e) =>
            {
                e.RefCount--;
                if (e.RefCount == 0)
                    shouldRemove = true;
                return e;
            });

            if (shouldRemove)
                _autoResetEvents.TryRemove(resource, out var _);
        }
        sw.Stop();

        _lockWaitTimeHistogram.Record(sw.Elapsed.TotalMilliseconds);

        if (!gotLock)
        {
            _lockTimeoutCounter.Add(1);

            if (cancellationToken.IsCancellationRequested && isTraceLogLevelEnabled)
                _logger.LogTrace("Cancellation requested for lock {Resource} ({LockId}) after {Duration:g}", resource, lockId, sw.Elapsed);
            else if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("Failed to acquire lock {Resource} ({LockId}) after {Duration:g}", resource, lockId, sw.Elapsed);

            return null;
        }

        if (sw.Elapsed > TimeSpan.FromSeconds(5) && _logger.IsEnabled(LogLevel.Warning))
            _logger.LogWarning("Acquired lock {Resource} ({LockId}) after {Duration:g}", resource, lockId, sw.Elapsed);
        else if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Acquired lock {Resource} ({LockId}) after {Duration:g}", resource, lockId, sw.Elapsed);

        return new DisposableLock(resource, lockId, sw.Elapsed, this, _logger, releaseOnDispose);
    }

    public async Task<bool> IsLockedAsync(string resource)
    {
        var result = await Run.WithRetriesAsync(() => _cacheClient.ExistsAsync(resource), logger: _logger).AnyContext();
        return result;
    }

    public async Task ReleaseAsync(string resource, string lockId)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("ReleaseAsync Start: {Resource} ({LockId})", resource, lockId);

        await Run.WithRetriesAsync(() => _cacheClient.RemoveIfEqualAsync(resource, lockId), 15, logger: _logger).AnyContext();
        await _messageBus.PublishAsync(new CacheLockReleased { Resource = resource, LockId = lockId }).AnyContext();

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Released lock: {Resource} ({LockId})", resource, lockId);
    }

    public Task RenewAsync(string resource, string lockId, TimeSpan? timeUntilExpires = null)
    {
        if (!timeUntilExpires.HasValue)
            timeUntilExpires = TimeSpan.FromMinutes(20);

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Renewing lock {Resource} ({LockId}) for {Duration:g}", resource, lockId, timeUntilExpires);

        return Run.WithRetriesAsync(() => _cacheClient.ReplaceIfEqualAsync(resource, lockId, lockId, timeUntilExpires.Value));
    }

    private class ResetEventWithRefCount
    {
        public int RefCount { get; set; }
        public AsyncAutoResetEvent Target { get; set; }
    }

    private static string _allowedChars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private static Random _rng = new();

    private string GenerateNewLockId()
    {
        char[] chars = new char[16];

        for (int i = 0; i < 16; ++i)
            chars[i] = _allowedChars[_rng.Next(62)];

        return new string(chars, 0, 16);
    }
}

public class CacheLockReleased
{
    public string Resource { get; set; }
    public string LockId { get; set; }
}
