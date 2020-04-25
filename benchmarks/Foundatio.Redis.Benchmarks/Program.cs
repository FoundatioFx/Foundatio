using System;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Foundatio.Benchmarks.Caching;
using Foundatio.Benchmarks.Queues;

namespace Foundatio.Benchmarks {
    public class Program {
        public static void Main(string[] args) {
            var summary = BenchmarkRunner.Run<QueueBenchmarks>();
            Console.WriteLine(summary.ToString());

            summary = BenchmarkRunner.Run<JobQueueBenchmarks>();
            Console.WriteLine(summary.ToString());

            summary = BenchmarkRunner.Run<CacheBenchmarks>();
            Console.WriteLine(summary.ToString());
            Console.ReadKey();
        }
    }

    public class BenchmarkConfig : ManualConfig {
        public BenchmarkConfig() {
            AddJob(Job.Default.WithWarmupCount(1).WithIterationCount(1));
        }
    }
}