using System;

namespace Foundatio.Queues {
    public interface IQueueEventHandler<T> where T : class {
        bool BeforeEnqueue(IQueue<T> queue, T data);
        void AfterEnqueue(IQueue<T> queue, string id, T data);
        void OnDequeue(IQueue<T> queue, string id, T data);
        void OnComplete(IQueue<T> queue, string id);
        void OnAbandon(IQueue<T> queue, string id);
    }
}
