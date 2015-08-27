using System;
using System.Threading.Tasks;
using Foundatio.Lock;

namespace Foundatio.Extensions {
    public static class LockProviderExtensions {
        public static async Task TryUsingLockAsync(this ILockProvider locker, string name, Action work, TimeSpan? lockTimeout = null, TimeSpan? acquireTimeout = null) {
            using (var l = await locker.AcquireLockAsync(name, lockTimeout, acquireTimeout))
                if (l != null)
                    work();
        }
    }
}