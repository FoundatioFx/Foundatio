using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Foundatio.Jobs;
using Foundatio.Logging;
using Foundatio.Queues;
using StackExchange.Redis;

namespace Foundatio.Benchmarks.Queues {
    public class JobQueueBenchmarks {
        private const int ITEM_COUNT = 1000;
        private readonly IQueue<QueueItem> _inMemoryQueue = new InMemoryQueue<QueueItem>(new InMemoryQueueOptions<QueueItem>());
        private readonly IQueue<QueueItem> _redisQueue = new RedisQueue<QueueItem>(new RedisQueueOptions<QueueItem> { ConnectionMultiplexer = ConnectionMultiplexer.Connect("localhost") });

        [IterationSetup]
        public void Setup() {
            _inMemoryQueue.DeleteQueueAsync().GetAwaiter().GetResult();
            _redisQueue.DeleteQueueAsync().GetAwaiter().GetResult();
        }

        [IterationSetup(Target = nameof(RunInMemoryJobUntilEmptyRedis))]
        public void EnqueueInMemoryQueue() {
            EnqueueQueue(_inMemoryQueue);
        }

        [Benchmark]
        public void RunInMemoryJobUntilEmptyRedis() {
            RunJobUntilEmpty(_inMemoryQueue);
        }

        [IterationSetup(Target = nameof(RunRedisQueueJobUntilEmptyRedis))]
        public void EnqueueRedisQueue() {
            EnqueueQueue(_redisQueue);
        }

        [Benchmark]
        public void RunRedisQueueJobUntilEmptyRedis() {
            RunJobUntilEmpty(_redisQueue);
        }

        private void EnqueueQueue(IQueue<QueueItem> queue) {
            try {
                for (int i = 0; i < ITEM_COUNT; i++)
                    queue.EnqueueAsync(new QueueItem { Id = i }).GetAwaiter().GetResult();
            } catch (Exception ex) {
                Console.WriteLine(ex);
            }
        }

        private void RunJobUntilEmpty(IQueue<QueueItem> queue) {
            var job = new BenchmarkJobQueue(queue);
            job.RunUntilEmptyAsync().GetAwaiter().GetResult();
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
