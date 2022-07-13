using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Queues {
    public interface IQueueEntry : IAsyncDisposable {
        string Id { get; }
        string CorrelationId { get; }
        IDictionary<string, string> Properties { get; }
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
    }
    
    public interface IQueueEntry<T> : IQueueEntry  where T : class {
        T Value { get; }
    }
}