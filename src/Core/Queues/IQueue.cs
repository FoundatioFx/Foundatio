using System;
using System.Collections.Generic;

namespace Foundatio.Queues {
    public interface IQueue<T> : IDisposable where T : class {
        string Enqueue(T data);
        void StartWorking(Action<QueueEntry<T>> handler, bool autoComplete = false);
        void StopWorking();
        QueueEntry<T> Dequeue(TimeSpan? timeout = null);
        void Complete(string id);
        void Abandon(string id);
        // TODO: Change to get all stats at the same time to allow optimization of retrieval.
        long GetQueueCount();
        long GetWorkingCount();
        long GetDeadletterCount();
        IEnumerable<T> GetDeadletterItems();
        void DeleteQueue();
        long EnqueuedCount { get; }
        long DequeuedCount { get; }
        long CompletedCount { get; }
        long AbandonedCount { get; }
        long WorkerErrorCount { get; }
        string QueueId { get; }
    }
}
