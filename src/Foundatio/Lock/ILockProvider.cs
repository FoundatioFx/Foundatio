using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Lock;

/// <summary>
/// Provides distributed locking to coordinate access to shared resources across processes.
/// </summary>
public interface ILockProvider
{
    /// <summary>
    /// Acquires a lock on the specified resource. Throws <see cref="LockAcquisitionTimeoutException"/>
    /// if the lock cannot be obtained before the supplied <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="resource">The resource identifier to lock. Use consistent naming across processes.</param>
    /// <param name="timeUntilExpires">
    /// How long the lock is held before automatic release. Defaults to 20 minutes.
    /// For long-running operations, call <see cref="ILock.RenewAsync"/> periodically.
    /// </param>
    /// <param name="releaseOnDispose">If true, the lock is released when disposed.</param>
    /// <param name="cancellationToken">
    /// Token used to abort the acquisition attempt. Pass a token from a <see cref="CancellationTokenSource"/>
    /// with a <c>CancelAfter</c> to apply a timeout. The method throws
    /// <see cref="LockAcquisitionTimeoutException"/> when the token is cancelled before acquisition completes.
    /// </param>
    /// <returns>The acquired <see cref="ILock"/>.</returns>
    /// <exception cref="LockAcquisitionTimeoutException">The lock could not be acquired before cancellation.</exception>
    /// <remarks>
    /// Use this when the work cannot run safely without the lock. For best-effort acquisition where
    /// failure is a normal control-flow outcome, call <see cref="TryAcquireAsync(string, TimeSpan?, bool, CancellationToken)"/>
    /// and check the result for <c>null</c>.
    /// </remarks>
    Task<ILock> AcquireAsync(string resource, TimeSpan? timeUntilExpires = null, bool releaseOnDispose = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to acquire a lock on the specified resource. Returns <c>null</c> if the lock
    /// cannot be obtained before the supplied <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="resource">The resource identifier to lock. Use consistent naming across processes.</param>
    /// <param name="timeUntilExpires">
    /// How long the lock is held before automatic release. Defaults to 20 minutes.
    /// For long-running operations, call <see cref="ILock.RenewAsync"/> periodically.
    /// </param>
    /// <param name="releaseOnDispose">If true, the lock is released when disposed.</param>
    /// <param name="cancellationToken">
    /// Token used to abort the acquisition attempt. Pass a token from a <see cref="CancellationTokenSource"/>
    /// with a <c>CancelAfter</c> to apply a timeout. When acquisition cannot complete before
    /// cancellation, the method returns <c>null</c> rather than throwing.
    /// </param>
    /// <returns>The acquired <see cref="ILock"/>, or <c>null</c> if the lock could not be obtained.</returns>
    /// <remarks>
    /// Use this when lock unavailability is a normal control-flow outcome (best-effort dedupe,
    /// opportunistic work). When the work cannot run without the lock, call
    /// <see cref="AcquireAsync(string, TimeSpan?, bool, CancellationToken)"/> instead.
    /// </remarks>
    Task<ILock?> TryAcquireAsync(string resource, TimeSpan? timeUntilExpires = null, bool releaseOnDispose = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether a resource is currently locked.
    /// </summary>
    /// <param name="resource">The resource identifier to check.</param>
    /// <returns>True if the resource is locked; otherwise, false.</returns>
    Task<bool> IsLockedAsync(string resource);

    /// <summary>
    /// Releases a specific lock on a resource.
    /// </summary>
    /// <param name="resource">The resource identifier.</param>
    /// <param name="lockId">The unique identifier of the lock to release.</param>
    Task ReleaseAsync(string resource, string lockId);

    /// <summary>
    /// Releases any lock on a resource regardless of lock ID.
    /// Use with caution as this may release locks held by other processes.
    /// </summary>
    /// <param name="resource">The resource identifier.</param>
    Task ReleaseAsync(string resource);

    /// <summary>
    /// Extends the expiration time of an existing lock.
    /// </summary>
    /// <param name="resource">The resource identifier.</param>
    /// <param name="lockId">The unique identifier of the lock to renew.</param>
    /// <param name="timeUntilExpires">The new expiration duration from now.</param>
    Task RenewAsync(string resource, string lockId, TimeSpan? timeUntilExpires = null);
}

/// <summary>
/// Represents an acquired lock on a resource. Dispose to release the lock.
/// </summary>
public interface ILock : IAsyncDisposable
{
    /// <summary>
    /// Extends the lock expiration to prevent automatic release during long-running operations.
    /// </summary>
    /// <param name="timeUntilExpires">The new expiration duration from now.</param>
    Task RenewAsync(TimeSpan? timeUntilExpires = null);

    /// <summary>
    /// Explicitly releases the lock, allowing other processes to acquire it.
    /// </summary>
    Task ReleaseAsync();

    /// <summary>
    /// Gets the unique identifier for this lock instance.
    /// </summary>
    string LockId { get; }

    /// <summary>
    /// Gets the resource identifier this lock is held on.
    /// </summary>
    string Resource { get; }

    /// <summary>
    /// Gets the UTC time when this lock was acquired.
    /// </summary>
    DateTime AcquiredTimeUtc { get; }

    /// <summary>
    /// Gets the duration spent waiting to acquire this lock.
    /// </summary>
    TimeSpan TimeWaitedForLock { get; }

    /// <summary>
    /// Gets the number of times this lock has been renewed.
    /// </summary>
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

    // ------------------------------------------------------------------
    // AcquireAsync — single resource. Throws LockAcquisitionTimeoutException
    // on failure. Use this when failure to acquire is genuinely exceptional
    // (e.g. a serialization lock guarding correctness).
    // ------------------------------------------------------------------

    /// <summary>
    /// Acquires the lock or throws <see cref="LockAcquisitionTimeoutException"/> if the
    /// supplied <paramref name="cancellationToken"/> is cancelled before the lock is acquired.
    /// </summary>
    /// <exception cref="LockAcquisitionTimeoutException">The lock could not be acquired before cancellation.</exception>
    public static Task<ILock> AcquireAsync(this ILockProvider provider, string resource, TimeSpan? timeUntilExpires = null, CancellationToken cancellationToken = default)
    {
        return provider.AcquireAsync(resource, timeUntilExpires, true, cancellationToken);
    }

    /// <summary>
    /// Acquires the lock, waiting up to <paramref name="acquireTimeout"/> before throwing
    /// <see cref="LockAcquisitionTimeoutException"/>. A null <paramref name="acquireTimeout"/>
    /// applies a 30-second default.
    /// </summary>
    /// <exception cref="LockAcquisitionTimeoutException">The lock could not be acquired before the timeout elapsed.</exception>
    public static async Task<ILock> AcquireAsync(this ILockProvider provider, string resource, TimeSpan? timeUntilExpires = null, TimeSpan? acquireTimeout = null)
    {
        using var cancellationTokenSource = acquireTimeout.ToCancellationTokenSource(TimeSpan.FromSeconds(30));
        return await provider.AcquireAsync(resource, timeUntilExpires, true, cancellationTokenSource.Token).AnyContext();
    }

    // ------------------------------------------------------------------
    // TryAcquireAsync — single resource. Returns null on failure.
    // Use this when lock unavailability is a normal control-flow outcome.
    // ------------------------------------------------------------------

    /// <summary>
    /// Tries to acquire the lock without a timeout. Returns <c>null</c> if the supplied
    /// <paramref name="cancellationToken"/> is cancelled before the lock is acquired.
    /// </summary>
    public static Task<ILock?> TryAcquireAsync(this ILockProvider provider, string resource, TimeSpan? timeUntilExpires = null, CancellationToken cancellationToken = default)
    {
        return provider.TryAcquireAsync(resource, timeUntilExpires, true, cancellationToken);
    }

    /// <summary>
    /// Tries to acquire the lock, waiting up to <paramref name="acquireTimeout"/> before giving up.
    /// Returns <c>null</c> if the timeout elapses without acquiring the lock. A null
    /// <paramref name="acquireTimeout"/> applies a 30-second default.
    /// </summary>
    public static async Task<ILock?> TryAcquireAsync(this ILockProvider provider, string resource, TimeSpan? timeUntilExpires = null, TimeSpan? acquireTimeout = null)
    {
        using var cancellationTokenSource = acquireTimeout.ToCancellationTokenSource(TimeSpan.FromSeconds(30));
        return await provider.TryAcquireAsync(resource, timeUntilExpires, true, cancellationTokenSource.Token).AnyContext();
    }

    // ------------------------------------------------------------------
    // TryUsingAsync — single resource. Best-effort: skips work if lock
    // can't be obtained, returns whether the work ran.
    // ------------------------------------------------------------------

    public static async Task<bool> TryUsingAsync(this ILockProvider locker, string resource, Func<CancellationToken, Task> work, TimeSpan? timeUntilExpires = null, CancellationToken cancellationToken = default)
    {
        await using var l = await locker.TryAcquireAsync(resource, timeUntilExpires, true, cancellationToken).AnyContext();
        if (l is null)
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
        await using var l = await locker.TryAcquireAsync(resource, timeUntilExpires, true, cancellationToken).AnyContext();
        if (l is null)
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
        await using var l = await locker.TryAcquireAsync(resource, timeUntilExpires, true, cancellationTokenSource.Token).AnyContext();
        if (l is null)
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
        await using var l = await locker.TryAcquireAsync(resource, timeUntilExpires, true, cancellationTokenSource.Token).AnyContext();
        if (l is null)
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

    // ------------------------------------------------------------------
    // TryAcquireAsync — multiple resources. Returns null on failure.
    // ------------------------------------------------------------------

    public static async Task<ILock?> TryAcquireAsync(this ILockProvider provider, IEnumerable<string> resources, TimeSpan? timeUntilExpires = null, bool releaseOnDispose = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resources);

        string[] resourceList = resources.Distinct().OrderBy(r => r).ToArray();
        if (resourceList.Length is 0)
            return EmptyLock.Empty;

        var logger = provider.GetLogger();

        // If renew time is greater than 0, then cut the time in half with max time of 1 minute.
        var renewTime = timeUntilExpires.GetValueOrDefault(TimeSpan.FromMinutes(1));
        if (renewTime > TimeSpan.Zero)
            renewTime = TimeSpan.FromTicks(renewTime.Ticks / 2) > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : TimeSpan.FromTicks(renewTime.Ticks / 2);

        logger.LogTrace("Acquiring {LockCount} locks {Resource} RenewTime={RenewTime:g}", resourceList.Length, resourceList, renewTime);
        var sw = Stopwatch.StartNew();

        var acquiredLocks = new List<(ILock Lock, DateTimeOffset LastRenewed)>(resourceList.Length);
        foreach (string resource in resourceList)
        {
            var l = await provider.TryAcquireAsync(resource, timeUntilExpires, releaseOnDispose, cancellationToken).AnyContext();
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
                logger.LogTrace("Unable to acquire all {LockCount} locks {UnacquiredResources} releasing acquired locks: {Resource}", unacquiredResources.Count, unacquiredResources, acquiredResources);

                await Task.WhenAll(locks.Select(l => l.ReleaseAsync())).AnyContext();

                logger.LogTrace("Released {LockCount} locks {Resource}", acquiredResources.Length, acquiredResources);

                return null;
            }
        }

        logger.LogTrace("Acquired {LockCount} locks {Resource} after {Duration:g}", resourceList.Length, resourceList, sw.Elapsed);

        return new DisposableLockCollection(locks, String.Join("+", locks.Select(l => l.LockId)), provider.GetTimeProvider().GetUtcNow().UtcDateTime, sw.Elapsed, logger);
    }

    public static async Task<ILock?> TryAcquireAsync(this ILockProvider provider, IEnumerable<string> resources, TimeSpan? timeUntilExpires, TimeSpan? acquireTimeout)
    {
        using var cancellationTokenSource = acquireTimeout.ToCancellationTokenSource(TimeSpan.FromSeconds(30));
        return await provider.TryAcquireAsync(resources, timeUntilExpires, true, cancellationTokenSource.Token).AnyContext();
    }

    public static async Task<ILock?> TryAcquireAsync(this ILockProvider provider, IEnumerable<string> resources, TimeSpan? timeUntilExpires, TimeSpan? acquireTimeout, bool releaseOnDispose)
    {
        using var cancellationTokenSource = acquireTimeout.ToCancellationTokenSource(TimeSpan.FromSeconds(30));
        return await provider.TryAcquireAsync(resources, timeUntilExpires, releaseOnDispose, cancellationTokenSource.Token).AnyContext();
    }

    // ------------------------------------------------------------------
    // AcquireAsync — multiple resources. Throws on failure.
    // ------------------------------------------------------------------

    /// <exception cref="LockAcquisitionTimeoutException">One or more locks could not be acquired.</exception>
    public static async Task<ILock> AcquireAsync(this ILockProvider provider, IEnumerable<string> resources, TimeSpan? timeUntilExpires = null, bool releaseOnDispose = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resources);

        // Materialize once so the exception message reports exactly what we attempted to acquire,
        // and so single-use enumerables don't get walked twice.
        string[] resourceList = resources as string[] ?? resources.ToArray();

        var l = await provider.TryAcquireAsync(resourceList, timeUntilExpires, releaseOnDispose, cancellationToken).AnyContext();
        if (l is null)
            throw new LockAcquisitionTimeoutException(String.Join(",", resourceList));

        return l;
    }

    /// <exception cref="LockAcquisitionTimeoutException">One or more locks could not be acquired before the timeout elapsed.</exception>
    public static async Task<ILock> AcquireAsync(this ILockProvider provider, IEnumerable<string> resources, TimeSpan? timeUntilExpires, TimeSpan? acquireTimeout)
    {
        ArgumentNullException.ThrowIfNull(resources);

        string[] resourceList = resources as string[] ?? resources.ToArray();

        using var cancellationTokenSource = acquireTimeout.ToCancellationTokenSource(TimeSpan.FromSeconds(30));
        var l = await provider.TryAcquireAsync(resourceList, timeUntilExpires, true, cancellationTokenSource.Token).AnyContext();
        if (l is null)
            throw new LockAcquisitionTimeoutException(String.Join(",", resourceList));

        return l;
    }

    /// <exception cref="LockAcquisitionTimeoutException">One or more locks could not be acquired before the timeout elapsed.</exception>
    public static async Task<ILock> AcquireAsync(this ILockProvider provider, IEnumerable<string> resources, TimeSpan? timeUntilExpires, TimeSpan? acquireTimeout, bool releaseOnDispose)
    {
        ArgumentNullException.ThrowIfNull(resources);

        string[] resourceList = resources as string[] ?? resources.ToArray();

        using var cancellationTokenSource = acquireTimeout.ToCancellationTokenSource(TimeSpan.FromSeconds(30));
        var l = await provider.TryAcquireAsync(resourceList, timeUntilExpires, releaseOnDispose, cancellationTokenSource.Token).AnyContext();
        if (l is null)
            throw new LockAcquisitionTimeoutException(String.Join(",", resourceList));

        return l;
    }

    // ------------------------------------------------------------------
    // TryUsingAsync — multiple resources.
    // ------------------------------------------------------------------

    public static async Task<bool> TryUsingAsync(this ILockProvider locker, IEnumerable<string> resources, Func<CancellationToken, Task> work, TimeSpan? timeUntilExpires = null, CancellationToken cancellationToken = default)
    {
        await using var l = await locker.TryAcquireAsync(resources, timeUntilExpires, true, cancellationToken).AnyContext();
        if (l is null)
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
        await using var l = await locker.TryAcquireAsync(resources, timeUntilExpires, true, cancellationToken).AnyContext();
        if (l is null)
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
        await using var l = await locker.TryAcquireAsync(resources, timeUntilExpires, true, cancellationTokenSource.Token).AnyContext();
        if (l is null)
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
        await using var l = await locker.TryAcquireAsync(resources, timeUntilExpires, true, cancellationTokenSource.Token).AnyContext();
        if (l is null)
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
