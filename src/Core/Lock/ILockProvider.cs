using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;

namespace Foundatio.Lock {
    public interface ILockProvider : IDisposable {
        Task<ILock> AcquireAsync(string name, TimeSpan? lockTimeout = null, CancellationToken cancellationToken = default(CancellationToken));
        Task<bool> IsLockedAsync(string name);
        Task ReleaseAsync(string name);
        Task RenewAsync(string name, TimeSpan? lockExtension = null);
    }

    public interface ILock : IDisposable {
        Task RenewAsync(TimeSpan? lockExtension = null);
    }

    public static class LockProviderExtensions {
        public static Task<ILock> AcquireAsync(this ILockProvider provider, string name, TimeSpan? lockTimeout = null, TimeSpan? acquireTimeout = null) {
            return provider.AcquireAsync(name, lockTimeout, acquireTimeout.ToCancellationToken(TimeSpan.FromMinutes(1)));
        }

        public static async Task TryUsingAsync(this ILockProvider locker, string name, Func<CancellationToken, Task> work, TimeSpan? lockTimeout = null, CancellationToken cancellationToken = default(CancellationToken)) {
            using (var l = await locker.AcquireAsync(name, lockTimeout, cancellationToken).AnyContext())
                if (l != null)
                    await work(cancellationToken).AnyContext();
        }

        public static async Task TryUsingAsync(this ILockProvider locker, string name, Func<Task> work, TimeSpan? lockTimeout = null, CancellationToken cancellationToken = default(CancellationToken)) {
            using (var l = await locker.AcquireAsync(name, lockTimeout, cancellationToken).AnyContext())
                if (l != null)
                    await work().AnyContext();
        }

        public static async Task TryUsingAsync(this ILockProvider locker, string name, Func<CancellationToken, Task> work, TimeSpan? lockTimeout = null, TimeSpan? acquireTimeout = null) {
            var cancellationToken = acquireTimeout?.ToCancellationToken() ?? default(CancellationToken);
            using (var l = await locker.AcquireAsync(name, lockTimeout, cancellationToken).AnyContext())
                if (l != null)
                    await work(cancellationToken).AnyContext();
        }

        public static async Task TryUsingAsync(this ILockProvider locker, string name, Func<Task> work, TimeSpan? lockTimeout = null, TimeSpan? acquireTimeout = null) {
            var cancellationToken = acquireTimeout?.ToCancellationToken() ?? default(CancellationToken);
            using (var l = await locker.AcquireAsync(name, lockTimeout, cancellationToken).AnyContext())
                if (l != null)
                    await work().AnyContext();
        }
    }
}
