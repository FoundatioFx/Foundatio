using System;

namespace Foundatio.Queues {
    public interface IQueueActivity {
        DateTime? LastEnqueueActivity { get; }
        DateTime? LastDequeueActivity { get; }
    }
}