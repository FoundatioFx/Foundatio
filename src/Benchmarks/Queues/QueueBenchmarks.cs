using System;
using BenchmarkDotNet.Attributes;
using Foundatio.Queues;
using StackExchange.Redis;

namespace Foundatio.Benchmarks.Queues {
    public class QueueBenchmarks {
        private const int _itemCount = 1000;

        [Benchmark]
        public void ProcessInMemoryQueue() {
            var _queue = new InMemoryQueue<QueueItem>();

            for (int i = 0; i < _itemCount; i++)
                _queue.EnqueueAsync(new QueueItem { Id = i }).GetAwaiter().GetResult();

            for (int i = 0; i < _itemCount; i++)
                _queue.DequeueAsync(TimeSpan.Zero).GetAwaiter().GetResult();
        }

        [Benchmark]
        public void ProcessRedisQueue() {
            var muxer = ConnectionMultiplexer.Connect("localhost");
            var _queue = new RedisQueue<QueueItem>(muxer);

            for (int i = 0; i < _itemCount; i++)
                _queue.EnqueueAsync(new QueueItem { Id = i }).GetAwaiter().GetResult();

            for (int i = 0; i < _itemCount; i++)
                _queue.DequeueAsync(TimeSpan.Zero).GetAwaiter().GetResult();
        }
    }

    public class QueueItem {
        public int Id { get; set; }
    }
}
