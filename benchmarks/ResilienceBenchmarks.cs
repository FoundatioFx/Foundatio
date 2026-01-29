using System;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Foundatio.Resilience;
using Polly;
using Polly.Retry;

namespace Foundatio.Benchmarks;

/// <summary>
/// Compares Foundatio vs Polly resilience policy overhead.
/// All benchmarks execute operations that always succeed to measure pure framework overhead.
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ResilienceBenchmarks
{
    // Foundatio policies
    private IResiliencePolicy _foundatioNoRetry;
    private IResiliencePolicy _foundatioWithRetry;

    // Polly pipelines (void operations)
    private ResiliencePipeline _pollyNoRetry;
    private ResiliencePipeline _pollyWithRetry;

    // Polly pipelines (with result)
    private ResiliencePipeline<int> _pollyNoRetryWithResult;
    private ResiliencePipeline<int> _pollyWithRetryWithResult;

    private int _counter;

    [GlobalSetup]
    public void Setup()
    {
        // ============================================================
        // NO RETRY CONFIGURATION - Measures base framework overhead
        // ============================================================

        // Foundatio: 1 attempt, no delay
        _foundatioNoRetry = new ResiliencePolicyBuilder()
            .WithMaxAttempts(1)
            .WithDelay(TimeSpan.Zero)
            .Build();

        // Polly: Empty pipeline (no retry strategy)
        _pollyNoRetry = new ResiliencePipelineBuilder()
            .Build();

        _pollyNoRetryWithResult = new ResiliencePipelineBuilder<int>()
            .Build();

        // ============================================================
        // WITH RETRY CONFIGURATION - 3 attempts total
        // ============================================================

        // Foundatio: 3 attempts
        _foundatioWithRetry = new ResiliencePolicyBuilder()
            .WithMaxAttempts(3)
            .WithDelay(TimeSpan.Zero)
            .Build();

        // Polly: 2 retries (3 total attempts)
        _pollyWithRetry = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.Zero
            })
            .Build();

        _pollyWithRetryWithResult = new ResiliencePipelineBuilder<int>()
            .AddRetry(new RetryStrategyOptions<int>
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.Zero
            })
            .Build();
    }

    // ============================================================
    // SCENARIO 1: No Retries
    // Measures pure framework overhead without retry configuration
    // ============================================================

    [BenchmarkCategory("1_NoRetry")]
    [Benchmark(Baseline = true)]
    public ValueTask Direct_NoRetry()
    {
        return SimulateWorkAsync();
    }

    [BenchmarkCategory("1_NoRetry")]
    [Benchmark]
    public ValueTask Foundatio_NoRetry()
    {
        return _foundatioNoRetry.ExecuteAsync(_ => SimulateWorkAsync(), CancellationToken.None);
    }

    [BenchmarkCategory("1_NoRetry")]
    [Benchmark]
    public ValueTask Polly_NoRetry()
    {
        return _pollyNoRetry.ExecuteAsync(_ => SimulateWorkAsync(), CancellationToken.None);
    }

    // ============================================================
    // SCENARIO 2: With Retries
    // Measures overhead when retry policy is configured (but not triggered)
    // ============================================================

    [BenchmarkCategory("2_WithRetry")]
    [Benchmark(Baseline = true)]
    public ValueTask Direct_WithRetry()
    {
        return SimulateWorkAsync();
    }

    [BenchmarkCategory("2_WithRetry")]
    [Benchmark]
    public ValueTask Foundatio_WithRetry()
    {
        return _foundatioWithRetry.ExecuteAsync(_ => SimulateWorkAsync(), CancellationToken.None);
    }

    [BenchmarkCategory("2_WithRetry")]
    [Benchmark]
    public ValueTask Polly_WithRetry()
    {
        return _pollyWithRetry.ExecuteAsync(_ => SimulateWorkAsync(), CancellationToken.None);
    }

    // ============================================================
    // SCENARIO 3: No Retries - Operation Returns Result
    // Measures framework overhead when returning values
    // ============================================================

    [BenchmarkCategory("3_NoRetry_WithResult")]
    [Benchmark(Baseline = true)]
    public ValueTask<int> Direct_NoRetry_WithResult()
    {
        return SimulateWorkWithResultAsync();
    }

    [BenchmarkCategory("3_NoRetry_WithResult")]
    [Benchmark]
    public ValueTask<int> Foundatio_NoRetry_WithResult()
    {
        return _foundatioNoRetry.ExecuteAsync(_ => SimulateWorkWithResultAsync(), CancellationToken.None);
    }

    [BenchmarkCategory("3_NoRetry_WithResult")]
    [Benchmark]
    public ValueTask<int> Polly_NoRetry_WithResult()
    {
        return _pollyNoRetryWithResult.ExecuteAsync(_ => SimulateWorkWithResultAsync(), CancellationToken.None);
    }

    // ============================================================
    // SCENARIO 4: With Retries - Operation Returns Result
    // Measures overhead with retry policy configured and returning values
    // ============================================================

    [BenchmarkCategory("4_WithRetry_WithResult")]
    [Benchmark(Baseline = true)]
    public ValueTask<int> Direct_WithRetry_WithResult()
    {
        return SimulateWorkWithResultAsync();
    }

    [BenchmarkCategory("4_WithRetry_WithResult")]
    [Benchmark]
    public ValueTask<int> Foundatio_WithRetry_WithResult()
    {
        return _foundatioWithRetry.ExecuteAsync(_ => SimulateWorkWithResultAsync(), CancellationToken.None);
    }

    [BenchmarkCategory("4_WithRetry_WithResult")]
    [Benchmark]
    public ValueTask<int> Polly_WithRetry_WithResult()
    {
        return _pollyWithRetryWithResult.ExecuteAsync(_ => SimulateWorkWithResultAsync(), CancellationToken.None);
    }

    // ============================================================
    // SIMULATED OPERATIONS
    // Minimal work to measure framework overhead, not operation cost
    // ============================================================

    private ValueTask SimulateWorkAsync()
    {
        Interlocked.Increment(ref _counter);
        return ValueTask.CompletedTask;
    }

    private ValueTask<int> SimulateWorkWithResultAsync()
    {
        return ValueTask.FromResult(Interlocked.Increment(ref _counter));
    }
}

