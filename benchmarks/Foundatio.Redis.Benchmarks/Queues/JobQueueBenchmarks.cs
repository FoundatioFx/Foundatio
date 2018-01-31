using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Foundatio.Jobs;
using Foundatio.Queues;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Foundatio.Benchmarks.Queues {
    public class JobQueueBenchmarks {
        private const int ITEM_COUNT = 1000;
        private readonly IQueue<QueueItem> _inMemoryQueue = new InMemoryQueue<QueueItem>();
        private readonly IQueue<QueueItem> _redisQueue = new RedisQueue<QueueItem>(o => o.ConnectionMultiplexer(ConnectionMultiplexer.Connect("localhost")));

        [IterationSetup]
        public void Setup() {
            _inMemoryQueue.DeleteQueueAsync().GetAwaiter().GetResult();
            _redisQueue.DeleteQueueAsync().GetAwaiter().GetResult();
        }

        [IterationSetup(Target = nameof(RunInMemoryJobUntilEmptyAsync))]
        public Task EnqueueInMemoryQueueAsync() {
            return EnqueueQueueAsync(_inMemoryQueue);
        }

        [Benchmark]
        public Task RunInMemoryJobUntilEmptyAsync() {
            return RunJobUntilEmptyAsync(_inMemoryQueue);
        }

        [IterationSetup(Target = nameof(RunRedisQueueJobUntilEmptyAsync))]
        public Task EnqueueRedisQueueAsync() {
            return EnqueueQueueAsync(_redisQueue);
        }

        [Benchmark]
        public Task RunRedisQueueJobUntilEmptyAsync() {
            return RunJobUntilEmptyAsync(_redisQueue);
        }

        private async Task EnqueueQueueAsync(IQueue<QueueItem> queue) {
            try {
                for (int i = 0; i < ITEM_COUNT; i++)
                    await queue.EnqueueAsync(new QueueItem { Id = i });
            } catch (Exception ex) {
                Console.WriteLine(ex);
            }
        }

        private Task RunJobUntilEmptyAsync(IQueue<QueueItem> queue) {
            var job = new BenchmarkJobQueue(queue);
            return job.RunUntilEmptyAsync();
        }
    }

    public class BenchmarkJobQueue : QueueJobBase<QueueItem> {
        public BenchmarkJobQueue(Lazy<IQueue<QueueItem>> queue, ILoggerFactory loggerFactory = null) : base(queue, loggerFactory) { }

        public BenchmarkJobQueue(IQueue<QueueItem> queue, ILoggerFactory loggerFactory = null) : base(queue, loggerFactory) { }

        protected override Task<JobResult> ProcessQueueEntryAsync(QueueEntryContext<QueueItem> context) {
            return Task.FromResult(JobResult.Success);
        }
    }
}
