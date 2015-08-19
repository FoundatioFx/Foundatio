using System;
using Foundatio.Lock;

namespace Foundatio.Extensions {
    public static class LockProviderExtensions {
        public static void TryUsingLock(this ILockProvider locker, string name, Action work, TimeSpan? lockTimeout = null, TimeSpan? acquireTimeout = null) {
            using (var l = locker.AcquireLock(name, lockTimeout, acquireTimeout))
                if (l != null)
                    work();
        }
    }
}