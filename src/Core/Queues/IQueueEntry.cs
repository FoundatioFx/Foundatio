using System.Threading.Tasks;

namespace Foundatio.Queues {
    public interface IQueueEntry<T> where T : class {
        string Id { get; }
        T Value { get; }
        Task AbandonAsync();
        Task CompleteAsync();
        void Dispose();
    }
}