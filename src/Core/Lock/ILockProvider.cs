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

        public static async Task TryUsingLockAsync(this ILockProvider locker, string name, Func<CancellationToken, Task> work, TimeSpan? lockTimeout = null, CancellationToken cancellationToken = default(CancellationToken)) {
            using (var l = await locker.AcquireLockAsync(name, lockTimeout, cancellationToken).AnyContext())
                if (l != null)
                    await work(cancellationToken).AnyContext();
        }

        public static async Task TryUsingLockAsync(this ILockProvider locker, string name, Func<Task> work, TimeSpan? lockTimeout = null, CancellationToken cancellationToken = default(CancellationToken)) {
            using (var l = await locker.AcquireLockAsync(name, lockTimeout, cancellationToken).AnyContext())
                if (l != null)
                    await work().AnyContext();
        }

        public static async Task TryUsingLockAsync(this ILockProvider locker, string name, Func<CancellationToken, Task> work, TimeSpan? lockTimeout = null, TimeSpan? acquireTimeout = null) {
            var cancellationToken = acquireTimeout?.ToCancellationToken() ?? default(CancellationToken);
            using (var l = await locker.AcquireLockAsync(name, lockTimeout, cancellationToken).AnyContext())
                if (l != null)
                    await work(cancellationToken).AnyContext();
        }

        public static async Task TryUsingLockAsync(this ILockProvider locker, string name, Func<Task> work, TimeSpan? lockTimeout = null, TimeSpan? acquireTimeout = null) {
            var cancellationToken = acquireTimeout?.ToCancellationToken() ?? default(CancellationToken);
            using (var l = await locker.AcquireLockAsync(name, lockTimeout, cancellationToken).AnyContext())
                if (l != null)
                    await work().AnyContext();
        }
    }
}
