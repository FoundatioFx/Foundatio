using System.Threading.Tasks;

namespace Foundatio.Queues {
    public interface IQueueEntry {
        string Id { get; }
        string CorrelationId { get; }
        DataDictionary Properties { get; }
        Type EntryType { get; }
        object GetValue();
        bool IsCompleted { get; }
        bool IsAbandoned { get; }
        int Attempts { get; }
        void MarkAbandoned();
        void MarkCompleted();
        Task RenewLockAsync();
        Task AbandonAsync();
        Task CompleteAsync();
        ValueTask DisposeAsync();
    }
    
    public interface IQueueEntry<T> : IQueueEntry  where T : class {
        T Value { get; }
    }
}