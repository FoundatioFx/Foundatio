using System;
using System.Threading.Tasks;
using Foundatio.Utility;

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

        protected virtual Task OnEnqueuing(object sender, EnqueuingEventArgs<T> enqueuingEventArgs) {
            return TaskHelper.Completed();
        }

        protected virtual Task OnEnqueued(object sender, EnqueuedEventArgs<T> enqueuedEventArgs) {
            return TaskHelper.Completed();
        }

        protected virtual Task OnDequeued(object sender, DequeuedEventArgs<T> dequeuedEventArgs) {
            return TaskHelper.Completed();
        }

        protected virtual Task OnCompleted(object sender, CompletedEventArgs<T> completedEventArgs) {
            return TaskHelper.Completed();
        }

        protected virtual Task OnAbandoned(object sender, AbandonedEventArgs<T> abandonedEventArgs) {
            return TaskHelper.Completed();
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
