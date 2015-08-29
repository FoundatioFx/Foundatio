using System;
using Foundatio.Caching;
using Foundatio.Logging;
using Foundatio.Messaging;
using Nito.AsyncEx;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Foundatio.Extensions;

namespace Foundatio.Lock {
    public class CacheLockProvider : ILockProvider {
        private readonly ICacheClient _cacheClient;
        private readonly IMessageBus _messageBus;
        private readonly ConcurrentDictionary<string, AsyncAutoResetEvent> _autoEvents = new ConcurrentDictionary<string, AsyncAutoResetEvent>();

        public CacheLockProvider(ICacheClient cacheClient, IMessageBus messageBus) {
            _cacheClient = new ScopedCacheClient(cacheClient, "lock");
            _messageBus = messageBus;
            _messageBus.SubscribeAsync<CacheLockReleased>(message => OnLockReleased(message)).AnyContext().GetAwaiter().GetResult();
        }

        private void OnLockReleased(CacheLockReleased msg) {
            Logger.Trace().Message("Got lock released message: {0}", msg.Name).Write();
            AsyncAutoResetEvent autoEvent;
            if (_autoEvents.TryGetValue(msg.Name, out autoEvent)) {
                autoEvent.Set();
            }
        }

        public async Task<IDisposable> AcquireLockAsync(string name, TimeSpan? lockTimeout = null, TimeSpan? acquireTimeout = null, CancellationToken cancellationToken = default(CancellationToken)) {
            Logger.Trace().Message("AcquireLock: {0}", name).Write();
            if (!lockTimeout.HasValue)
                lockTimeout = TimeSpan.FromMinutes(20);
            if (!acquireTimeout.HasValue)
                acquireTimeout = TimeSpan.FromMinutes(1);

            var tokenSource = new CancellationTokenSource(acquireTimeout.Value);
            var timeoutTime = DateTime.UtcNow.Add(acquireTimeout.Value);
            Logger.Trace().Message("Timeout time: {0}", timeoutTime.ToString("mm: ss.fff")).Write();
            bool allowLock = false;

            do {
                var now = DateTime.UtcNow;
                bool gotLock = false;
                if (lockTimeout.Value == TimeSpan.Zero) // no lock timeout
                    gotLock = await _cacheClient.AddAsync(name, now).AnyContext();
                else
                    gotLock = await _cacheClient.AddAsync(name, now, lockTimeout.Value).AnyContext();

                if (gotLock) {
                    allowLock = true;
                    break;
                }

                Logger.Trace().Message("Failed to acquire lock: {0}", name).Write();
                var keyExpiration = DateTime.UtcNow.Add(await _cacheClient.GetExpirationAsync(name).AnyContext() ?? TimeSpan.FromSeconds(1));
                if (keyExpiration < DateTime.UtcNow)
                    keyExpiration = DateTime.UtcNow;

                var delayAmount = timeoutTime < keyExpiration ? timeoutTime.Subtract(now) : keyExpiration.Subtract(now);
                Logger.Trace().Message("Delay time: {0}", delayAmount).Write();
                var autoEvent = _autoEvents.GetOrAdd(name, new AsyncAutoResetEvent(false));
                
                // wait for key expiration, release message or timeout
                Task.WaitAny(Task.Delay(delayAmount, tokenSource.Token), autoEvent.WaitAsync(tokenSource.Token));
                if (tokenSource.IsCancellationRequested) {
                    Logger.Trace().Message("Cancellation requested.").Write();
                    break;
                }
            } while (!tokenSource.IsCancellationRequested && DateTime.UtcNow <= timeoutTime);

            if (!allowLock)
                return null;

            Logger.Trace().Message("Returning lock: {0}", name).Write();
            return new DisposableLock(name, this);
        }

        public async Task<bool> IsLockedAsync(string name) {
            return await _cacheClient.GetAsync<object>(name).AnyContext() != null;
        }

        public async Task ReleaseLockAsync(string name) {
            Logger.Trace().Message("ReleaseLock: {0}", name).Write();
            await _cacheClient.RemoveAsync(name).AnyContext();
            await _messageBus.PublishAsync(new CacheLockReleased { Name = name }).AnyContext();
        }
        
        public void Dispose() { }
    }

    internal class CacheLockReleased {
        public string Name { get; set; }
    }
}