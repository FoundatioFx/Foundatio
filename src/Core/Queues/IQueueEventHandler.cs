using System;
using System.Threading.Tasks;

namespace Foundatio.Queues {
    public interface IQueueEventHandler<T> where T : class {
        bool BeforeEnqueue(IQueue<T> queue, T data);
        void AfterEnqueue(IQueue<T> queue, string id, T data);
        void OnDequeue(IQueue<T> queue, string id, T data);
        void OnComplete(IQueue<T> queue, string id);
        void OnAbandon(IQueue<T> queue, string id);
    }

    public interface IQueueEventHandler2<T> where T : class
    {
        Task<bool> BeforeEnqueueAsync(IQueue<T> queue, T data);
        Task AfterEnqueueAsync(IQueue<T> queue, string id, T data);
        Task OnDequeueAsync(IQueue<T> queue, string id, T data);
        Task OnCompleteAsync(IQueue<T> queue, string id);
        Task OnAbandonAsync(IQueue<T> queue, string id);
    }
}
