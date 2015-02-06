using System;
using System.Diagnostics;
using Foundatio.Queues;
using Foundatio.Tests.Queue;
using Foundatio.Tests.Utility;
using StackExchange.Redis;

namespace Foundatio.Redis.Tests.Queue {
    public class RedisQueueTests : InMemoryQueueTests {
        private ConnectionMultiplexer _muxer;

        protected override IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null) {
            if (ConnectionStrings.Get("RedisConnectionString") == null)
                return null;

            if (_muxer == null)
                _muxer = ConnectionMultiplexer.Connect(ConnectionStrings.Get("RedisConnectionString"));

            var queue = new RedisQueue<SimpleWorkItem>(_muxer, workItemTimeout: workItemTimeout, retries: retries, retryDelay: retryDelay);
            Debug.WriteLine(String.Format("Queue Id: {0}", queue.QueueId));
            return queue;
        }
    }
}