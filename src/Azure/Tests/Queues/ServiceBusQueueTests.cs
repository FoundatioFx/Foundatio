using System;
using Foundatio.Azure.Queues;
using Foundatio.Queues;
using Foundatio.Tests.Queue;
using Foundatio.Tests.Utility;

namespace Foundatio.Azure.Tests.Queue {
    public class ServiceBusQueueTests : InMemoryQueueTests {
        protected override IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null) {
            if (ConnectionStrings.Get("ServiceBusConnectionString") == null)
                return null;

            return new ServiceBusQueue<SimpleWorkItem>(ConnectionStrings.Get("ServiceBusConnectionString"), Guid.NewGuid().ToString("N"), retries, workItemTimeout);
        }
    }
}