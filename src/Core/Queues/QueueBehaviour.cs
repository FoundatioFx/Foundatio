using System;

namespace Foundatio.Queues {
    public interface IQueueBehavior<T> where T : class
    {
        void Attach(IQueue<T> queue);
    }

    public abstract class QueueBehaviorBase<T> : IQueueBehavior<T>, IDisposable where T: class
    {
        private IQueue<T> _queue;

        public void Attach(IQueue<T> queue)
        {
            _queue = queue;

            _queue.Enqueuing += OnEnqueuing;
            _queue.Enqueued += OnEnqueued;
            _queue.Dequeued += OnDequeued;
            _queue.Completed += OnCompleted;
            _queue.Abandoned += OnAbandoned;
        }

        protected virtual void OnEnqueuing(object sender, EnqueuingEventArgs<T> enqueuingEventArgs) { }
        protected virtual void OnEnqueued(object sender, EnqueuedEventArgs<T> enqueuedEventArgs) { }
        protected virtual void OnDequeued(object sender, DequeuedEventArgs<T> dequeuedEventArgs) { }
        protected virtual void OnCompleted(object sender, CompletedEventArgs<T> completedEventArgs) { }
        protected virtual void OnAbandoned(object sender, AbandonedEventArgs<T> abandonedEventArgs) { }

        public void Dispose()
        {
            _queue.Enqueuing -= OnEnqueuing;
            _queue.Enqueued -= OnEnqueued;
            _queue.Dequeued -= OnDequeued;
            _queue.Completed -= OnCompleted;
            _queue.Abandoned -= OnAbandoned;
        }
    }
}
