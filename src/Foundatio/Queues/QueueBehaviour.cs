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
            return Task.CompletedTask;
        }

        protected virtual Task OnEnqueued(object sender, EnqueuedEventArgs<T> enqueuedEventArgs) {
            return Task.CompletedTask;
        }

        protected virtual Task OnDequeued(object sender, DequeuedEventArgs<T> dequeuedEventArgs) {
            return Task.CompletedTask;
        }

        protected virtual Task OnLockRenewed(object sender, LockRenewedEventArgs<T> dequeuedEventArgs) {
            return Task.CompletedTask;
        }

        protected virtual Task OnCompleted(object sender, CompletedEventArgs<T> completedEventArgs) {
            return Task.CompletedTask;
        }

        protected virtual Task OnAbandoned(object sender, AbandonedEventArgs<T> abandonedEventArgs) {
            return Task.CompletedTask;
        }

        public virtual void Dispose() {
            foreach (var disposable in _disposables)
                disposable.Dispose();
        }
    }
}
