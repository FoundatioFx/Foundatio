using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Utility;

namespace Foundatio.Lock {
    public interface ILockProvider {
        Task<ILock> AcquireAsync(string name, TimeSpan? lockTimeout = null, CancellationToken cancellationToken = default(CancellationToken));
        Task<bool> IsLockedAsync(string name);
        Task ReleaseAsync(string name);
        Task RenewAsync(string name, TimeSpan? lockExtension = null);
    }

    public interface ILock : IAsyncDisposable {
        Task RenewAsync(TimeSpan? lockExtension = null);
        Task ReleaseAsync();
    }

    public static class LockProviderExtensions {
        public static Task<ILock> AcquireAsync(this ILockProvider provider, string name, TimeSpan? lockTimeout = null, TimeSpan? acquireTimeout = null) {
            return provider.AcquireAsync(name, lockTimeout, acquireTimeout.ToCancellationToken(acquireTimeout ?? TimeSpan.FromSeconds(30)));
        }

        public static async Task<bool> TryUsingAsync(this ILockProvider locker, string name, Func<CancellationToken, Task> work, TimeSpan? lockTimeout = null, CancellationToken cancellationToken = default(CancellationToken)) {
            var l = await locker.AcquireAsync(name, lockTimeout, cancellationToken).AnyContext();
            if (l == null)
                return false;

            try {
                await work(cancellationToken).AnyContext();
            } finally {
                await l.ReleaseAsync().AnyContext();
            }

            return true;
        }

        public static async Task<bool> TryUsingAsync(this ILockProvider locker, string name, Func<Task> work, TimeSpan? lockTimeout = null, CancellationToken cancellationToken = default(CancellationToken)) {
            var l = await locker.AcquireAsync(name, lockTimeout, cancellationToken).AnyContext();
            if (l == null)
                return false;

            try {
                await work().AnyContext();
            } finally {
                await l.ReleaseAsync().AnyContext();
            }

            return true;
        }

        public static async Task<bool> TryUsingAsync(this ILockProvider locker, string name, Func<CancellationToken, Task> work, TimeSpan? lockTimeout = null, TimeSpan? acquireTimeout = null) {
            var cancellationToken = acquireTimeout?.ToCancellationToken() ?? default(CancellationToken);
            var l = await locker.AcquireAsync(name, lockTimeout, cancellationToken).AnyContext();
            if (l == null)
                return false;

            try {
                await work(cancellationToken).AnyContext();
            } finally {
                await l.ReleaseAsync().AnyContext();
            }

            return true;
        }

        public static async Task<bool> TryUsingAsync(this ILockProvider locker, string name, Func<Task> work, TimeSpan? lockTimeout = null, TimeSpan? acquireTimeout = null) {
            var cancellationToken = acquireTimeout?.ToCancellationToken() ?? default(CancellationToken);
            var l = await locker.AcquireAsync(name, lockTimeout, cancellationToken).AnyContext();
            if (l == null)
                return false;

            try {
                await work().AnyContext();
            } finally {
                await l.ReleaseAsync().AnyContext();
            }

            return true;
        }

        public static Task<bool> TryUsingAsync(this ILockProvider locker, string name, Action work, TimeSpan? lockTimeout = null, TimeSpan? acquireTimeout = null) {
            return locker.TryUsingAsync(name, () => {
                work();
                return TaskHelper.Completed;
            }, lockTimeout, acquireTimeout);
        }
    }
}
