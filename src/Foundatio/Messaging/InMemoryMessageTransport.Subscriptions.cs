using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging;

public sealed partial class InMemoryMessageTransport
{
    private sealed class TemporarySubscription
    {
        public required DateTimeOffset ExpiresUtc { get; set; }
        public required ITimer Timer { get; init; }
    }

    private readonly Dictionary<string, TemporarySubscription> _temporarySubscriptions = new(StringComparer.OrdinalIgnoreCase);

    private void CreateTemporarySubscription(DestinationAddress source, TimeSpan lease)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        if (source.Role != DestinationRole.Subscription)
            throw new ArgumentException("Only subscriptions can have an expiration lease.", nameof(source));
        string key = StorageKey(source);
        if (_temporarySubscriptions.TryGetValue(key, out var existing))
        {
            existing.ExpiresUtc = _timeProvider.GetUtcNow().Add(lease);
            existing.Timer.Change(lease, Timeout.InfiniteTimeSpan);
            return;
        }
        var timer = _timeProvider.CreateTimer(_ => ExpireTemporarySubscription(key), null, lease, Timeout.InfiniteTimeSpan);
        _temporarySubscriptions[key] = new TemporarySubscription { ExpiresUtc = _timeProvider.GetUtcNow().Add(lease), Timer = timer };
    }

    private void ExpireTemporarySubscription(string key)
    {
        lock (_temporarySubscriptions)
        {
            if (!_temporarySubscriptions.TryGetValue(key, out var subscription) || subscription.ExpiresUtc > _timeProvider.GetUtcNow())
                return;
            _temporarySubscriptions.Remove(key);
            subscription.Timer.Dispose();
            DeleteDestination(key);
        }
    }

    public Task<bool> RenewSubscriptionAsync(DestinationAddress source, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        string key = StorageKey(source);
        lock (_temporarySubscriptions)
        {
            if (!_temporarySubscriptions.TryGetValue(key, out var subscription) || subscription.ExpiresUtc <= _timeProvider.GetUtcNow())
            {
                ExpireTemporarySubscription(key);
                return Task.FromResult(false);
            }
            subscription.ExpiresUtc = _timeProvider.GetUtcNow().Add(lease);
            subscription.Timer.Change(lease, Timeout.InfiniteTimeSpan);
            return Task.FromResult(true);
        }
    }

    private void DisposeTemporarySubscriptions()
    {
        lock (_temporarySubscriptions)
        {
            foreach (var subscription in _temporarySubscriptions.Values)
                subscription.Timer.Dispose();
            _temporarySubscriptions.Clear();
        }
    }
}
