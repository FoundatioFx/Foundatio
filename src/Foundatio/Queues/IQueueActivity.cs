using System;

namespace Foundatio.Queues;

public interface IQueueActivity
{
    DateTimeOffset? LastEnqueueActivity { get; }
    DateTimeOffset? LastDequeueActivity { get; }
}
