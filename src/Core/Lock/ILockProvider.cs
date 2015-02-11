using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Lock {
    public interface ILockProvider {
        IDisposable AcquireLock(string name, TimeSpan? lockTimeout = null, TimeSpan? acquireTimeout = null);
        bool IsLocked(string name);
        void ReleaseLock(string name);
    }

    public interface ILockProvider2 {
        Task<IDisposable> AcquireLockAsync(string name, TimeSpan? lockTimeout = null, TimeSpan? acquireTimeout = null, CancellationToken cancellationToken = default(CancellationToken));
        bool IsLocked(string name);
        void ReleaseLock(string name);
    }
}
