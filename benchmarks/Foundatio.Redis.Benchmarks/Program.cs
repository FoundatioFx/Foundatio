using System;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Foundatio.Benchmarks.Caching;
using Foundatio.Benchmarks.Queues;

namespace Foundatio.Benchmarks {
    class Program {
        static void Main(string[] args) {
            var summary = BenchmarkRunner.Run<CacheBenchmarks>();
            Console.WriteLine(summary.ToString());

            summary = BenchmarkRunner.Run<QueueBenchmarks>();
            Console.WriteLine(summary.ToString());
            Console.ReadKey();
        }
    }

    class BenchmarkConfig : ManualConfig {
        public BenchmarkConfig() {
            Add(Job.Default.WithWarmupCount(1).WithTargetCount(1));
        }
    }
}