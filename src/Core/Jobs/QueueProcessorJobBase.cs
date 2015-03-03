using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Queues;
using NLog.Fluent;

namespace Foundatio.Jobs {
    public abstract class QueueProcessorJobBase<T> : JobBase where T : class {
        private readonly IQueue<T> _queue;

        public QueueProcessorJobBase(IQueue<T> queue) {
            _queue = queue;
        }

        protected override Task<JobResult> RunInternalAsync(CancellationToken token) {
            QueueEntry<T> queueEntry = null;
            try {
                queueEntry = _queue.Dequeue();
            } catch (Exception ex) {
                if (!(ex is TimeoutException)) {
                    Log.Error().Exception(ex).Message("Error trying to dequeue message: {0}", ex.Message).Write();
                    return Task.FromResult(JobResult.FromException(ex));
                }
            }

            if (queueEntry == null)
                return Task.FromResult(JobResult.Success);
            
            Log.Trace().Message("Processing message '{0}'.", queueEntry.Id).Write();

            return ProcessQueueItem(queueEntry);
        }

        public void RunUntilEmpty() {
            do {
                while (_queue.GetQueueCount() > 0)
                    Run();

                Thread.Sleep(100);
            } while (_queue.GetQueueCount() != 0);
        }

        protected abstract Task<JobResult> ProcessQueueItem(QueueEntry<T> queueEntry);
    }
}
