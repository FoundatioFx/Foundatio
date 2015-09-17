using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Queues;
using Foundatio.Utility;
using Foundatio.Logging;

namespace Foundatio.Jobs {
    public abstract class QueueProcessorJobBase<T> : JobBase, IQueueProcessorJob where T : class {
        protected readonly IQueue<T> _queue;

        public QueueProcessorJobBase(IQueue<T> queue) {
            _queue = queue;
        }

        protected bool AutoComplete { get; set; }

        protected override async Task<JobResult> RunInternalAsync(CancellationToken token) {
            var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(token, TimeSpan.FromSeconds(30).ToCancellationToken());

            QueueEntry<T> queueEntry;
            try {
                queueEntry = await _queue.DequeueAsync(linkedCancellationToken.Token).AnyContext();
            } catch (Exception ex) {
                Logger.Error().Exception(ex).Message("Error trying to dequeue message: {0}", ex.Message).Write();
                return JobResult.FromException(ex);
            }

            if (queueEntry == null)
                return JobResult.Success;
            
            using (var lockValue = await GetQueueItemLockAsync(queueEntry).AnyContext()) {
                if (lockValue == null)
                    return JobResult.SuccessWithMessage("Unable to acquire queue item lock.");

                Logger.Trace().Message("Processing queue entry '{0}'.", queueEntry.Id).Write();
                try {
                    var result = await ProcessQueueItemAsync(queueEntry).AnyContext();

                    if (!AutoComplete)
                        return result;

                    if (result.IsSuccess)
                        await queueEntry.CompleteAsync().AnyContext();
                    else
                        await queueEntry.AbandonAsync().AnyContext();

                    return result;
                } catch {
                    await queueEntry.AbandonAsync().AnyContext();
                    throw;
                }
            }
        }

        protected virtual Task<IDisposable> GetQueueItemLockAsync(QueueEntry<T> queueEntry) {
            return Task.FromResult(Disposable.Empty);
        }
        
        public async Task RunUntilEmptyAsync(CancellationToken cancellationToken = default(CancellationToken)) {
            await RunContinuousAsync(cancellationToken: cancellationToken, continuationCallback: async () => {
                var stats = await _queue.GetQueueStatsAsync().AnyContext();
                Logger.Trace().Message("RunUntilEmpty continuation: queue: {0} working={1}", stats.Queued, stats.Working).Write();
                return stats.Queued + stats.Working > 0;
            }).AnyContext();
        }

        protected abstract Task<JobResult> ProcessQueueItemAsync(QueueEntry<T> queueEntry);
    }

    public interface IQueueProcessorJob : IDisposable {
        Task RunUntilEmptyAsync(CancellationToken cancellationToken = default(CancellationToken));
    }
}
