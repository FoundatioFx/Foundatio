using System;
using BenchmarkDotNet.Attributes;
using Foundatio.Queues;
using StackExchange.Redis;

namespace Foundatio.Benchmarks.Queues {
    public class QueueBenchmarks {
        private const int _itemCount = 100;
        private readonly IQueue<QueueItem> _inMemoryQueue = new InMemoryQueue<QueueItem>();
        private readonly IQueue<QueueItem> _redisQueue;

        public QueueBenchmarks() {
            var muxer = ConnectionMultiplexer.Connect("localhost");
            _redisQueue = new RedisQueue<QueueItem>(muxer);
            _redisQueue.DeleteQueueAsync().GetAwaiter().GetResult();
        }

        [Benchmark]
        public void ProcessInMemoryQueue() {
            try {
                for (int i = 0; i < _itemCount; i++)
                    _inMemoryQueue.EnqueueAsync(new QueueItem { Id = i }).GetAwaiter().GetResult();
            } catch (Exception ex) {
                Console.WriteLine(ex);
            }

            try {
                for (int i = 0; i < _itemCount; i++) {
                    var entry = _inMemoryQueue.DequeueAsync(TimeSpan.Zero).GetAwaiter().GetResult();
                    entry.CompleteAsync().GetAwaiter().GetResult();
                }
            } catch (Exception ex) {
                Console.WriteLine(ex);
            }
        }

        [Benchmark]
        public void ProcessRedisQueue() {
            try {
                for (int i = 0; i < _itemCount; i++)
                    _redisQueue.EnqueueAsync(new QueueItem { Id = i }).GetAwaiter().GetResult();
            } catch (Exception ex) {
                Console.WriteLine(ex);
            }

            try {
                for (int i = 0; i < _itemCount; i++) {
                    var entry = _redisQueue.DequeueAsync(TimeSpan.Zero).GetAwaiter().GetResult();
                    entry.CompleteAsync().GetAwaiter().GetResult();
                }
            } catch (Exception ex) {
                Console.WriteLine(ex);
            }
        }
    }

    public class QueueItem {
        public int Id { get; set; }
    }
}
