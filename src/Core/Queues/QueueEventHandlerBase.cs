namespace Foundatio.Queues {
    public abstract class QueueEventHandlerBase<T> : IQueueEventHandler<T> where T : class {
        public virtual bool BeforeEnqueue(IQueue<T> queue, T data) {
            return true;
        }

        public virtual void AfterEnqueue(IQueue<T> queue, string id, T data) { }
        public virtual void OnDequeue(IQueue<T> queue, string id, T data) { }
        public virtual void OnComplete(IQueue<T> queue, string id) { }
        public virtual void OnAbandon(IQueue<T> queue, string id) { }
    }
}