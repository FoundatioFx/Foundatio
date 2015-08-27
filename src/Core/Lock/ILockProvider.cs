using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Lock {
    public interface ILockProvider : IDisposable {
        Task<IDisposable> AcquireLockAsync(string name, TimeSpan? lockTimeout = null, TimeSpan? acquireTimeout = null, CancellationToken cancellationToken = default(CancellationToken));
        Task<bool> IsLockedAsync(string name);
        Task ReleaseLockAsync(string name);
    }
}
