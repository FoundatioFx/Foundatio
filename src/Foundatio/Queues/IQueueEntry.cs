using System;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Queues {
    public interface IQueueEntry<T> where T : class {
        string Id { get; }
        string CorrelationId { get; }
        string ParentId { get; }
        DataDictionary Properties { get; }
        bool IsCompleted { get; }
        bool IsAbandoned { get; }
        int Attempts { get; }
        void MarkAbandoned();
        void MarkCompleted();
        T Value { get; }
        Task RenewLockAsync();
        Task AbandonAsync();
        Task CompleteAsync();
        ValueTask DisposeAsync();
    }
}