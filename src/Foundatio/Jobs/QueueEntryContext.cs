using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Foundatio.Lock;
using Foundatio.Queues;

namespace Foundatio.Jobs {
    public class QueueEntryContext<T> : JobContext where T : class {
        public QueueEntryContext(IQueueEntry<T> queueEntry, ILock queueEntryLock, CancellationToken cancellationToken = default(CancellationToken)) : base(cancellationToken, queueEntryLock) {
            QueueEntry = queueEntry;
        }

        public IQueueEntry<T> QueueEntry { get; private set; }

        public override async Task RenewLockAsync() {
            if (QueueEntry != null)
                await QueueEntry.RenewLockAsync().AnyContext();

            await base.RenewLockAsync().AnyContext();
        }
    }
}