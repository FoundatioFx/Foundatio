using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Lock;

public interface ILockProvider
{
    Task<ILock> AcquireAsync(string resource, TimeSpan? timeUntilExpires = null, bool releaseOnDispose = true, CancellationToken cancellationToken = default);
    Task<bool> IsLockedAsync(string resource);
    Task ReleaseAsync(string resource, string lockId);
    Task RenewAsync(string resource, string lockId, TimeSpan? timeUntilExpires = null);
}

public interface ILock : IAsyncDisposable
{
    Task RenewAsync(TimeSpan? timeUntilExpires = null);
    Task ReleaseAsync();
    string LockId { get; }
    string Resource { get; }
    DateTime AcquiredTimeUtc { get; }
    TimeSpan TimeWaitedForLock { get; }
    int RenewalCount { get; }
}

public static class LockProviderExtensions
{
    public static Task ReleaseAsync(this ILockProvider provider, ILock @lock)
    {
        return provider.ReleaseAsync(@lock.Resource, @lock.LockId);
    }

    public static Task RenewAsync(this ILockProvider provider, ILock @lock, TimeSpan? timeUntilExpires = null)
    {
        return provider.RenewAsync(@lock.Resource, @lock.LockId, timeUntilExpires);
    }

    public static Task<ILock> AcquireAsync(this ILockProvider provider, string resource, TimeSpan? timeUntilExpires = null, CancellationToken cancellationToken = default)
    {
        return provider.AcquireAsync(resource, timeUntilExpires, true, cancellationToken);
    }

    public static async Task<ILock> AcquireAsync(this ILockProvider provider, string resource, TimeSpan? timeUntilExpires = null, TimeSpan? acquireTimeout = null)
    {
        using var cancellationTokenSource = acquireTimeout.ToCancellationTokenSource(TimeSpan.FromSeconds(30));
        return await provider.AcquireAsync(resource, timeUntilExpires, true, cancellationTokenSource.Token).AnyContext();
    }

    public static async Task<bool> TryUsingAsync(this ILockProvider locker, string resource, Func<CancellationToken, Task> work, TimeSpan? timeUntilExpires = null, CancellationToken cancellationToken = default)
    {
        var l = await locker.AcquireAsync(resource, timeUntilExpires, true, cancellationToken).AnyContext();
        if (l == null)
            return false;

        try
        {
            await work(cancellationToken).AnyContext();
        }
        finally
        {
            await l.ReleaseAsync().AnyContext();
        }

        return true;
    }

    public static async Task<bool> TryUsingAsync(this ILockProvider locker, string resource, Func<Task> work, TimeSpan? timeUntilExpires = null, CancellationToken cancellationToken = default)
    {
        var l = await locker.AcquireAsync(resource, timeUntilExpires, true, cancellationToken).AnyContext();
        if (l == null)
            return false;

        try
        {
            await work().AnyContext();
        }
        finally
        {
            await l.ReleaseAsync().AnyContext();
        }

        return true;
    }

    public static async Task<bool> TryUsingAsync(this ILockProvider locker, string resource, Func<CancellationToken, Task> work, TimeSpan? timeUntilExpires = null, TimeSpan? acquireTimeout = null)
    {
        using var cancellationTokenSource = acquireTimeout.ToCancellationTokenSource();
        var l = await locker.AcquireAsync(resource, timeUntilExpires, true, cancellationTokenSource.Token).AnyContext();
        if (l == null)
            return false;

        try
        {
            await work(cancellationTokenSource.Token).AnyContext();
        }
        finally
        {
            await l.ReleaseAsync().AnyContext();
        }

        return true;
    }

    public static async Task<bool> TryUsingAsync(this ILockProvider locker, string resource, Func<Task> work, TimeSpan? timeUntilExpires = null, TimeSpan? acquireTimeout = null)
    {
        using var cancellationTokenSource = acquireTimeout.ToCancellationTokenSource();
        var l = await locker.AcquireAsync(resource, timeUntilExpires, true, cancellationTokenSource.Token).AnyContext();
        if (l == null)
            return false;

        try
        {
            await work().AnyContext();
        }
        finally
        {
            await l.ReleaseAsync().AnyContext();
        }

        return true;
    }

    public static Task<bool> TryUsingAsync(this ILockProvider locker, string resource, Action work, TimeSpan? timeUntilExpires = null, TimeSpan? acquireTimeout = null)
    {
        return locker.TryUsingAsync(resource, () =>
        {
            work();
            return Task.CompletedTask;
        }, timeUntilExpires, acquireTimeout);
    }

    public static async Task<ILock> AcquireAsync(this ILockProvider provider, IEnumerable<string> resources, TimeSpan? timeUntilExpires = null, bool releaseOnDispose = true, CancellationToken cancellationToken = default)
    {
        if (resources == null)
            throw new ArgumentNullException(nameof(resources));

        string[] resourceList = resources.Distinct().OrderBy(r => r).ToArray();
        if (resourceList.Length == 0)
            return new EmptyLock();

        var logger = provider.GetLogger();

        // If renew time is greater than 0, then cut the time in half with max time of 1 minute.
        var renewTime = timeUntilExpires.GetValueOrDefault(TimeSpan.FromMinutes(1));
        if (renewTime > TimeSpan.Zero)
            renewTime = TimeSpan.FromTicks(renewTime.Ticks / 2) > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : TimeSpan.FromTicks(renewTime.Ticks / 2);

        bool isTraceLogLevelEnabled = logger.IsEnabled(LogLevel.Trace);
        if (isTraceLogLevelEnabled)
            logger.LogTrace("Acquiring {LockCount} locks {Resource} RenewTime={RenewTime:g}", resourceList.Length, resourceList, renewTime);

        var sw = Stopwatch.StartNew();

        var acquiredLocks = new List<(ILock Lock, DateTimeOffset LastRenewed)>(resourceList.Length);
        foreach (string resource in resourceList)
        {
            var l = await provider.AcquireAsync(resource, timeUntilExpires, releaseOnDispose, cancellationToken).AnyContext();
            if (l is null)
            {
                break;
            }

            // Renew any acquired locks, so they stay alive until we have all locks
            if (acquiredLocks.Count > 0 && renewTime > TimeSpan.Zero)
            {
                var utcNow = provider.GetTimeProvider().GetUtcNow();
                var locksToRenew = acquiredLocks.Where(al => al.LastRenewed < utcNow.Subtract(renewTime)).ToArray();
                if (locksToRenew.Length > 0)
                {
                    await Task.WhenAll(locksToRenew.Select(al => al.Lock.RenewAsync(timeUntilExpires))).AnyContext();
                    locksToRenew.ForEach(al => al.LastRenewed = utcNow);

                    if (isTraceLogLevelEnabled)
                        logger.LogTrace("Renewed {LockCount} locks {Resource} RenewTime={RenewTime:g}", locksToRenew.Length, locksToRenew.Select(al => al.Lock.Resource), renewTime);
                }
            }

            acquiredLocks.Add((l, provider.GetTimeProvider().GetUtcNow()));
        }

        sw.Stop();

        var locks = acquiredLocks.Select(l => l.Lock).ToArray();
        // if any lock is null, release any acquired and return null (all or nothing)
        if (resourceList.Length > locks.Length)
        {
            var unacquiredResources = new List<string>(resourceList.Length);
            string[] acquiredResources = locks.Select(l => l.Resource).ToArray();

            foreach (string resource in resourceList)
            {
                // account for scoped lock providers with prefixes
                if (!acquiredResources.Any(r => r.EndsWith(resource)))
                    unacquiredResources.Add(resource);
            }

            if (unacquiredResources.Count > 0)
            {
                if (isTraceLogLevelEnabled)
                    logger.LogTrace("Unable to acquire all {LockCount} locks {UnacquiredResources} releasing acquired locks: {Resource}", unacquiredResources.Count, unacquiredResources, acquiredResources);

                await Task.WhenAll(locks.Select(l => l.ReleaseAsync())).AnyContext();

                if (isTraceLogLevelEnabled)
                    logger.LogTrace("Released {LockCount} locks {Resource}", acquiredResources.Length, acquiredResources);

                return null;
            }
        }

        if (isTraceLogLevelEnabled)
            logger.LogTrace("Acquired {LockCount} locks {Resource} after {Duration:g}", resourceList.Length, resourceList, sw.Elapsed);

        return new DisposableLockCollection(locks, String.Join("+", locks.Select(l => l.LockId)), provider.GetTimeProvider().GetUtcNow().UtcDateTime, sw.Elapsed, logger);
    }

    public static async Task<ILock> AcquireAsync(this ILockProvider provider, IEnumerable<string> resources, TimeSpan? timeUntilExpires, TimeSpan? acquireTimeout)
    {
        using var cancellationTokenSource = acquireTimeout.ToCancellationTokenSource(TimeSpan.FromSeconds(30));
        return await provider.AcquireAsync(resources, timeUntilExpires, true, cancellationTokenSource.Token).AnyContext();
    }

    public static async Task<ILock> AcquireAsync(this ILockProvider provider, IEnumerable<string> resources, TimeSpan? timeUntilExpires, TimeSpan? acquireTimeout, bool releaseOnDispose)
    {
        using var cancellationTokenSource = acquireTimeout.ToCancellationTokenSource(TimeSpan.FromSeconds(30));
        return await provider.AcquireAsync(resources, timeUntilExpires, releaseOnDispose, cancellationTokenSource.Token).AnyContext();
    }

    public static async Task<bool> TryUsingAsync(this ILockProvider locker, IEnumerable<string> resources, Func<CancellationToken, Task> work, TimeSpan? timeUntilExpires = null, CancellationToken cancellationToken = default)
    {
        var l = await locker.AcquireAsync(resources, timeUntilExpires, true, cancellationToken).AnyContext();
        if (l == null)
            return false;

        try
        {
            await work(cancellationToken).AnyContext();
        }
        finally
        {
            await l.ReleaseAsync().AnyContext();
        }

        return true;
    }

    public static async Task<bool> TryUsingAsync(this ILockProvider locker, IEnumerable<string> resources, Func<Task> work, TimeSpan? timeUntilExpires = null, CancellationToken cancellationToken = default)
    {
        var l = await locker.AcquireAsync(resources, timeUntilExpires, true, cancellationToken).AnyContext();
        if (l == null)
            return false;

        try
        {
            await work().AnyContext();
        }
        finally
        {
            await l.ReleaseAsync().AnyContext();
        }

        return true;
    }

    public static async Task<bool> TryUsingAsync(this ILockProvider locker, IEnumerable<string> resources, Func<CancellationToken, Task> work, TimeSpan? timeUntilExpires, TimeSpan? acquireTimeout)
    {
        using var cancellationTokenSource = acquireTimeout.ToCancellationTokenSource();
        var l = await locker.AcquireAsync(resources, timeUntilExpires, true, cancellationTokenSource.Token).AnyContext();
        if (l == null)
            return false;

        try
        {
            await work(cancellationTokenSource.Token).AnyContext();
        }
        finally
        {
            await l.ReleaseAsync().AnyContext();
        }

        return true;
    }

    public static async Task<bool> TryUsingAsync(this ILockProvider locker, IEnumerable<string> resources, Func<Task> work, TimeSpan? timeUntilExpires, TimeSpan? acquireTimeout)
    {
        using var cancellationTokenSource = acquireTimeout.ToCancellationTokenSource();
        var l = await locker.AcquireAsync(resources, timeUntilExpires, true, cancellationTokenSource.Token).AnyContext();
        if (l == null)
            return false;

        try
        {
            await work().AnyContext();
        }
        finally
        {
            await l.ReleaseAsync().AnyContext();
        }

        return true;
    }

    public static Task<bool> TryUsingAsync(this ILockProvider locker, IEnumerable<string> resources, Action work, TimeSpan? timeUntilExpires, TimeSpan? acquireTimeout)
    {
        return locker.TryUsingAsync(resources, () =>
        {
            work();
            return Task.CompletedTask;
        }, timeUntilExpires, acquireTimeout);
    }
}
