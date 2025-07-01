using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Foundatio.Benchmarks;

var config = DefaultConfig.Instance;
var summary = BenchmarkRunner.Run<Benchmarks>(config, args);
