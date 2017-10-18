using System.Threading;
using System.Threading.Tasks;
using Foundatio.Lock;

namespace Foundatio.Jobs {
    public class JobContext {
        public JobContext(CancellationToken cancellationToken, ILock lck = null) {
            Lock = lck;
            CancellationToken = cancellationToken;
        }

        public ILock Lock { get; }
        public CancellationToken CancellationToken { get; }

        public virtual Task RenewLockAsync() {
            if (Lock != null)
                return Lock.RenewAsync();

            return Task.CompletedTask;
        }
    }
}