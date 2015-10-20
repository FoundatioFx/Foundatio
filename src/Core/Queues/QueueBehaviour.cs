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

            _queue.Enqueuing.RemoveHandler(OnEnqueuing);
            _queue.Enqueuing.RemoveHandler(OnEnqueuing);
            _queue.Enqueued.RemoveHandler(OnEnqueued);
            _queue.Enqueued.RemoveHandler(OnEnqueued);
            _queue.Dequeued.RemoveHandler(OnDequeued);
            _queue.Dequeued.AddHandler(OnDequeued);
            _queue.Completed.AddHandler(OnCompleted);
            _queue.Completed.AddHandler(OnCompleted);
            _queue.Abandoned.AddHandler(OnAbandoned);
            _queue.Abandoned.AddHandler(OnAbandoned);
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
            _queue.Enqueuing.RemoveHandler(OnEnqueuing);
            _queue.Enqueued.RemoveHandler(OnEnqueued);
            _queue.Dequeued.RemoveHandler(OnDequeued);
            _queue.Completed.RemoveHandler(OnCompleted);
            _queue.Abandoned.RemoveHandler(OnAbandoned);
        }
    }
}
