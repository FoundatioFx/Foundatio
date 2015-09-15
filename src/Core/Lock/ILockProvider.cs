using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Lock {
    public interface ILockProvider : IDisposable {
        Task<IDisposable> AcquireLockAsync(string name, TimeSpan? lockTimeout = null, CancellationToken cancellationToken = default(CancellationToken));
        Task<bool> IsLockedAsync(string name);
        Task ReleaseLockAsync(string name);
    }

    public static class LockProviderExtensions {
        public static Task<IDisposable> AcquireLockAsync(this ILockProvider provider, string name, TimeSpan? lockTimeout = null, TimeSpan? acquireTimeout = null) {
            var cancellationToken = default(CancellationToken);
            if (acquireTimeout.HasValue) {
                if (acquireTimeout.Value == TimeSpan.Zero)
                    cancellationToken = new CancellationToken(true);
                else if (acquireTimeout.Value.Ticks > 0)
                    cancellationToken = new CancellationTokenSource(acquireTimeout.Value).Token;
            } else {
                cancellationToken = new CancellationTokenSource(TimeSpan.FromMinutes(1)).Token;
            }

            return provider.AcquireLockAsync(name, lockTimeout, cancellationToken);
        }
    }
}
