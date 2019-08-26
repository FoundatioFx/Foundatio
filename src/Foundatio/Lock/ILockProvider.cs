using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Lock {
    public interface ILockProvider {
        Task<ILock> AcquireAsync(string resource, TimeSpan? timeUntilExpires = null, CancellationToken cancellationToken = default);
        Task<bool> IsLockedAsync(string resource);
        Task ReleaseAsync(ILock @lock);
        Task RenewAsync(ILock @lock, TimeSpan? timeUntilExpires = null);
    }

    public interface ILock : IAsyncDisposable {
        Task RenewAsync(TimeSpan? timeUntilExpires = null);
        Task ReleaseAsync();
        string LockId { get; }
        string Resource { get; }
        DateTime AcquiredTimeUtc { get; }
        TimeSpan TimeWaitedForLock { get; }
        int RenewalCount { get; }
    }

    public static class LockProviderExtensions {
        public static async Task<ILock> AcquireAsync(this ILockProvider provider, string resource, TimeSpan? timeUntilExpires = null, TimeSpan? acquireTimeout = null) {
            using (var cancellationTokenSource = acquireTimeout.ToCancellationTokenSource(TimeSpan.FromSeconds(30))) {
                return await provider.AcquireAsync(resource, timeUntilExpires, cancellationTokenSource.Token).AnyContext();
            }
        }

        public static async Task<bool> TryUsingAsync(this ILockProvider locker, string resource, Func<CancellationToken, Task> work, TimeSpan? timeUntilExpires = null, CancellationToken cancellationToken = default) {
            var l = await locker.AcquireAsync(resource, timeUntilExpires, cancellationToken).AnyContext();
            if (l == null)
                return false;

            try {
                await work(cancellationToken).AnyContext();
            } finally {
                await l.ReleaseAsync().AnyContext();
            }

            return true;
        }

        public static async Task<bool> TryUsingAsync(this ILockProvider locker, string resource, Func<Task> work, TimeSpan? timeUntilExpires = null, CancellationToken cancellationToken = default) {
            var l = await locker.AcquireAsync(resource, timeUntilExpires, cancellationToken).AnyContext();
            if (l == null)
                return false;

            try {
                await work().AnyContext();
            } finally {
                await l.ReleaseAsync().AnyContext();
            }

            return true;
        }

        public static async Task<bool> TryUsingAsync(this ILockProvider locker, string resource, Func<CancellationToken, Task> work, TimeSpan? timeUntilExpires = null, TimeSpan? acquireTimeout = null) {
            using (var cancellationTokenSource = acquireTimeout.ToCancellationTokenSource()) {
                var l = await locker.AcquireAsync(resource, timeUntilExpires, cancellationTokenSource.Token).AnyContext();
                if (l == null)
                    return false;

                try {
                    await work(cancellationTokenSource.Token).AnyContext();
                } finally {
                    await l.ReleaseAsync().AnyContext();
                }
            }

            return true;
        }

        public static async Task<bool> TryUsingAsync(this ILockProvider locker, string resource, Func<Task> work, TimeSpan? timeUntilExpires = null, TimeSpan? acquireTimeout = null) {
            using (var cancellationTokenSource = acquireTimeout.ToCancellationTokenSource()) {
                var l = await locker.AcquireAsync(resource, timeUntilExpires, cancellationTokenSource.Token).AnyContext();
                if (l == null)
                    return false;

                try {
                    await work().AnyContext();
                } finally {
                    await l.ReleaseAsync().AnyContext();
                }
            }

            return true;
        }

        public static Task<bool> TryUsingAsync(this ILockProvider locker, string resource, Action work, TimeSpan? timeUntilExpires = null, TimeSpan? acquireTimeout = null) {
            return locker.TryUsingAsync(resource, () => {
                work();
                return Task.CompletedTask;
            }, timeUntilExpires, acquireTimeout);
        }
    }
}
