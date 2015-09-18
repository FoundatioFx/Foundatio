using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;

namespace Foundatio.Lock {
    public interface ILockProvider : IDisposable {
        Task<IDisposable> AcquireLockAsync(string name, TimeSpan? lockTimeout = null, CancellationToken cancellationToken = default(CancellationToken));
        Task<bool> IsLockedAsync(string name);
        Task ReleaseLockAsync(string name);
    }

    public static class LockProviderExtensions {
        public static Task<IDisposable> AcquireLockAsync(this ILockProvider provider, string name, TimeSpan? lockTimeout = null, TimeSpan? acquireTimeout = null) {
            return provider.AcquireLockAsync(name, lockTimeout, acquireTimeout.ToCancellationToken(TimeSpan.FromMinutes(1)));
        }
    }
}
