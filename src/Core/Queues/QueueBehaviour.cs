using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Queues {
    public interface IQueueBehavior<T> where T : class {
        void Attach(IQueue<T> queue);
    }

    public abstract class QueueBehaviorBase<T> : IQueueBehavior<T>, IDisposable where T : class {
        protected IQueue<T> _queue;
        private readonly List<IDisposable> _disposables = new List<IDisposable>();

        public virtual void Attach(IQueue<T> queue) {
            _queue = queue;

            _disposables.Add(_queue.Enqueuing.AddHandler(OnEnqueuing));
            _disposables.Add(_queue.Enqueued.AddHandler(OnEnqueued));
            _disposables.Add(_queue.Dequeued.AddHandler(OnDequeued));
            _disposables.Add(_queue.LockRenewed.AddHandler(OnLockRenewed));
            _disposables.Add(_queue.Completed.AddHandler(OnCompleted));
            _disposables.Add(_queue.Abandoned.AddHandler(OnAbandoned));
        }

        protected virtual Task OnEnqueuing(object sender, EnqueuingEventArgs<T> enqueuingEventArgs) {
            return TaskHelper.Completed;
        }

        protected virtual Task OnEnqueued(object sender, EnqueuedEventArgs<T> enqueuedEventArgs) {
            return TaskHelper.Completed;
        }

        protected virtual Task OnDequeued(object sender, DequeuedEventArgs<T> dequeuedEventArgs) {
            return TaskHelper.Completed;
        }

        protected virtual Task OnLockRenewed(object sender, LockRenewedEventArgs<T> dequeuedEventArgs) {
            return TaskHelper.Completed;
        }

        protected virtual Task OnCompleted(object sender, CompletedEventArgs<T> completedEventArgs) {
            return TaskHelper.Completed;
        }

        protected virtual Task OnAbandoned(object sender, AbandonedEventArgs<T> abandonedEventArgs) {
            return TaskHelper.Completed;
        }

        public void Dispose() {
            foreach (var disposable in _disposables)
                disposable.Dispose();
        }
    }
}
