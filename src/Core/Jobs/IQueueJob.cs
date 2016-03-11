using Foundatio.Queues;

namespace Foundatio.Jobs {
    public interface IQueueJob : IJob {
        IQueue Queue { get; }
    }
}