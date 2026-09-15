using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace Foundatio.Lock;

/// <summary>Redis resource locks with ownership-checked renewal and release.</summary>
public sealed class RedisLockProvider(IConnectionMultiplexer connection, string keyPrefix = "fnd:locks:") : ILockProvider
{
    private const string ReleaseScript = "if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) else return 0 end";
    private const string RenewScript = "if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('PEXPIRE', KEYS[1], ARGV[2]) else return 0 end";

    public async Task<ILock> AcquireAsync(string resource, TimeSpan? timeUntilExpires = null, bool releaseOnDispose = true, CancellationToken cancellationToken = default)
        => await TryAcquireAsync(resource, timeUntilExpires, releaseOnDispose, cancellationToken).ConfigureAwait(false)
            ?? throw new LockAcquisitionTimeoutException(resource);

    public async Task<ILock?> TryAcquireAsync(string resource, TimeSpan? timeUntilExpires = null, bool releaseOnDispose = true, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        var lifetime = timeUntilExpires ?? TimeSpan.FromMinutes(20);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        string id = Guid.NewGuid().ToString("N");
        long started = Stopwatch.GetTimestamp();
        var database = connection.GetDatabase();
        do
        {
            // Observe every acquisition outcome even if cancellation arrives during Redis I/O, so a late successful acquisition can still be released by the caller.
            if (await database.StringSetAsync(keyPrefix + resource, id, lifetime, When.NotExists).ConfigureAwait(false))
                return new Handle(this, resource, id, Stopwatch.GetElapsedTime(started), releaseOnDispose);
            if (cancellationToken.IsCancellationRequested) return null;
            try { await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return null; }
        } while (true);
    }

    public Task<bool> IsLockedAsync(string resource) => connection.GetDatabase().KeyExistsAsync(keyPrefix + resource);
    public async Task ReleaseAsync(string resource, string lockId)
        => _ = await connection.GetDatabase().ScriptEvaluateAsync(ReleaseScript, [keyPrefix + resource], [lockId]).ConfigureAwait(false);
    public async Task ReleaseAsync(string resource)
        => _ = await connection.GetDatabase().KeyDeleteAsync(keyPrefix + resource).ConfigureAwait(false);
    public async Task RenewAsync(string resource, string lockId, TimeSpan? timeUntilExpires = null)
    {
        var lifetime = timeUntilExpires ?? TimeSpan.FromMinutes(20);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        if ((int)await connection.GetDatabase().ScriptEvaluateAsync(RenewScript, [keyPrefix + resource], [lockId, (long)Math.Ceiling(lifetime.TotalMilliseconds)]).ConfigureAwait(false) == 0)
            throw new LockOwnershipLostException($"The lock on '{resource}' is no longer owned by this holder.");
    }

    private sealed class Handle(RedisLockProvider owner, string resource, string id, TimeSpan waited, bool releaseOnDispose) : ILock
    {
        private int _released;
        private int _renewals;
        public string LockId => id;
        public string Resource => resource;
        public DateTime AcquiredTimeUtc { get; } = DateTime.UtcNow;
        public TimeSpan TimeWaitedForLock => waited;
        public int RenewalCount => Volatile.Read(ref _renewals);
        public async Task RenewAsync(TimeSpan? timeUntilExpires = null)
        {
            if (Volatile.Read(ref _released) != 0) throw new LockOwnershipLostException("This lock was already released.");
            await owner.RenewAsync(resource, id, timeUntilExpires).ConfigureAwait(false);
            Interlocked.Increment(ref _renewals);
        }
        public async Task ReleaseAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) await owner.ReleaseAsync(resource, id).ConfigureAwait(false);
        }
        public ValueTask DisposeAsync() => releaseOnDispose ? new ValueTask(ReleaseAsync()) : default;
    }
}
