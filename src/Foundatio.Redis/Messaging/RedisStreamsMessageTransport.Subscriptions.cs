using System;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace Foundatio.Messaging;

public sealed partial class RedisStreamsMessageTransport
{
    private async Task EnsureTemporarySubscriptionAsync(DestinationAddress source, TimeSpan lease, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        if (source.Role != DestinationRole.Subscription)
            throw new ArgumentException("Only subscriptions can have expiration leases.", nameof(source));
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = Resolve(source);
        long now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await _db.ScriptEvaluateAsync(TopicRetentionFunctions + """

            cleanupSubscriptions(KEYS[1], ARGV[2])
            local created = redis.pcall('XGROUP', 'CREATE', KEYS[1], ARGV[1], '$', 'MKSTREAM')
            if type(created) == 'table' and created.err then
                if not string.find(created.err, 'BUSYGROUP', 1, true) then return redis.error_reply(created.err) end
                if not redis.call('ZSCORE', KEYS[2], ARGV[1]) then return redis.error_reply('Cannot change a durable subscription into a temporary subscription.') end
            end
            redis.call('ZADD', KEYS[2], ARGV[3], ARGV[1])
            return 1
            """, new RedisKey[] { resolved.StreamKey, (RedisKey)$"{resolved.StreamKey}:subscriptions" },
            new RedisValue[] { resolved.Group, now, now + (long)lease.TotalMilliseconds }).ConfigureAwait(false);
        _ensuredGroups.TryAdd(GroupKey(resolved), 0);
    }

    public async Task<bool> RenewSubscriptionAsync(DestinationAddress source, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = Resolve(source);
        long now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var result = await _db.ScriptEvaluateAsync("""
            local expires = tonumber(redis.call('ZSCORE', KEYS[1], ARGV[1]) or '0')
            if expires <= tonumber(ARGV[2]) then return 0 end
            redis.call('ZADD', KEYS[1], ARGV[3], ARGV[1])
            return 1
            """, new RedisKey[] { (RedisKey)$"{resolved.StreamKey}:subscriptions" },
            new RedisValue[] { resolved.Group, now, now + (long)lease.TotalMilliseconds }).ConfigureAwait(false);
        return (long)result == 1;
    }
}
