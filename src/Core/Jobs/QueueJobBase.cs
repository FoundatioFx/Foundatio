using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Lock;
using Foundatio.Logging;
using Foundatio.Queues;
using Foundatio.Utility;

namespace Foundatio.Jobs {
    public abstract class QueueJobBase<T> : IQueueJob, IHaveLogger where T : class {
        protected readonly ILogger _logger;
        protected readonly IQueue<T> _queue;
        protected readonly string _queueEntryName = typeof(T).Name;

        public QueueJobBase(IQueue<T> queue, ILoggerFactory loggerFactory = null) {
            _queue = queue;
            _logger = loggerFactory.CreateLogger(GetType());
            AutoComplete = true;
        }

        protected bool AutoComplete { get; set; }
        public string JobId { get; } = Guid.NewGuid().ToString("N").Substring(0, 10);
        IQueue IQueueJob.Queue => _queue;
        ILogger IHaveLogger.Logger => _logger;

        public async Task<JobResult> RunAsync(CancellationToken cancellationToken = new CancellationToken()) {
            var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, TimeSpan.FromSeconds(30).ToCancellationToken());

            IQueueEntry<T> queueEntry;
            try {
                queueEntry = await _queue.DequeueAsync(linkedCancellationToken.Token).AnyContext();
            } catch (Exception ex) {
                return JobResult.FromException(ex, $"Error trying to dequeue message: {ex.Message}");
            }

            if (queueEntry == null)
                return JobResult.Success;

            if (cancellationToken.IsCancellationRequested) {
                _logger.Info(() => $"Job was cancelled. Abandoning queue item: {queueEntry.Id}");
                await queueEntry.AbandonAsync().AnyContext();
                return JobResult.Cancelled;
            }

            var lockValue = await GetQueueEntryLockAsync(queueEntry, cancellationToken).AnyContext();
            if (lockValue == null) {
                await queueEntry.AbandonAsync().AnyContext();
                _logger.Trace("Unable to acquire queue entry lock.");
                return JobResult.Success;
            }

            try {
                _logger.Info(() => $"Processing {_queueEntryName} queue entry ({queueEntry.Id}).");

                var result = await ProcessQueueEntryAsync(new QueueEntryContext<T>(queueEntry, lockValue, cancellationToken)).AnyContext();

                if (!AutoComplete || queueEntry.IsCompleted || queueEntry.IsAbandoned)
                    return result;

                if (result.IsSuccess) {
                    await queueEntry.CompleteAsync().AnyContext();
                    _logger.Info(() => $"Auto completed {_queueEntryName} queue entry ({queueEntry.Id}).");
                } else {
                    await queueEntry.AbandonAsync().AnyContext();
                    _logger.Warn(() => $"Auto abandoned {_queueEntryName} queue entry ({queueEntry.Id}).");
                }

                return result;
            } catch (Exception ex) {
                await queueEntry.AbandonAsync().AnyContext();
                _logger.Error(ex, () => $"Error processing {_queueEntryName} queue entry ({queueEntry.Id}).");

                throw;
            } finally {
                await lockValue.ReleaseAsync().AnyContext();
            }
        }

        protected abstract Task<JobResult> ProcessQueueEntryAsync(QueueEntryContext<T> context);

        protected virtual Task<ILock> GetQueueEntryLockAsync(IQueueEntry<T> queueEntry, CancellationToken cancellationToken = default(CancellationToken)) {
            return Task.FromResult(Disposable.EmptyLock);
        }
    }
}