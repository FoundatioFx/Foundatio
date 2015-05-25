using System;
using Foundatio.Lock;

namespace Foundatio.Extensions {
    public static class LockProviderExtensions {
        public static void TryUsingLock(this ILockProvider locker, string name, Action work, TimeSpan? lockTimeout = null, TimeSpan? acquireTimeout = null) {
            try {
                using (locker.AcquireLock(name, lockTimeout, acquireTimeout))
                    work();
            } catch (TimeoutException) {}
        }

        public static IDisposable TryAcquireLock(this ILockProvider locker, string name, TimeSpan? lockTimeout = null, TimeSpan? acquireTimeout = null) {
            try {
                return locker.AcquireLock(name, lockTimeout, acquireTimeout);
            } catch (TimeoutException) {}

            return null;
        }
    }
}