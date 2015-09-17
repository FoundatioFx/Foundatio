using System;
using Foundatio.Logging;

namespace Foundatio.Queues {
    public interface IQueueBehavior<T> where T : class {
        void Attach(IQueue<T> queue);
    }

    public abstract class QueueBehaviorBase<T> : IQueueBehavior<T>, IDisposable where T : class {
        protected IQueue<T> _queue;

        public virtual void Attach(IQueue<T> queue) {
            _queue = queue;

            _queue.Enqueuing -= OnEnqueuing;
            _queue.Enqueuing += OnEnqueuing;
            _queue.Enqueued -= OnEnqueued;
            _queue.Enqueued += OnEnqueued;
            _queue.Dequeued -= OnDequeued;
            _queue.Dequeued += OnDequeued;
            _queue.Completed -= OnCompleted;
            _queue.Completed += OnCompleted;
            _queue.Abandoned -= OnAbandoned;
            _queue.Abandoned += OnAbandoned;
        }

        protected virtual void OnEnqueuing(object sender, EnqueuingEventArgs<T> enqueuingEventArgs) {
            Logger.Trace().Message($"OnEnqueuing: Queue: {enqueuingEventArgs.Queue.QueueId}").Write();
        }

        protected virtual void OnEnqueued(object sender, EnqueuedEventArgs<T> enqueuedEventArgs) {
            Logger.Trace().Message($"OnEnqueued: Queue: {enqueuedEventArgs.Queue.QueueId} Id: {enqueuedEventArgs.Metadata.Id}").Write();
        }

        protected virtual void OnDequeued(object sender, DequeuedEventArgs<T> dequeuedEventArgs) {
            Logger.Trace().Message($"OnDequeued: Queue: {dequeuedEventArgs.Queue.QueueId} Id: {dequeuedEventArgs.Metadata.Id}").Write();
        }

        protected virtual void OnCompleted(object sender, CompletedEventArgs<T> completedEventArgs) {
            Logger.Trace().Message($"OnCompleted: Queue: {completedEventArgs.Queue.QueueId} Id: {completedEventArgs.Metadata.Id}").Write();
        }

        protected virtual void OnAbandoned(object sender, AbandonedEventArgs<T> abandonedEventArgs) {
            Logger.Trace().Message($"OnAbandoned: Queue: {abandonedEventArgs.Queue.QueueId} Id: {abandonedEventArgs.Metadata.Id}").Write();
        }

        public void Dispose() {
            _queue.Enqueuing -= OnEnqueuing;
            _queue.Enqueued -= OnEnqueued;
            _queue.Dequeued -= OnDequeued;
            _queue.Completed -= OnCompleted;
            _queue.Abandoned -= OnAbandoned;
        }
    }
}
