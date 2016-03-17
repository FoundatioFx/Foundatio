using System.Threading.Tasks;

namespace Foundatio.Queues {
    public interface IQueueEntry<T> where T : class {
        string Id { get; }
        bool IsCompleted { get; }
        bool IsAbandoned { get; }
        T Value { get; }
        Task RenewLockAsync();
        Task AbandonAsync();
        Task CompleteAsync();
        Task DisposeAsync();
    }
}