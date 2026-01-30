using System;
using System.Runtime.CompilerServices;
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
    // SCENARIO 1: Sync - No Retries
    // Measures pure framework overhead for synchronous execution
    // ============================================================

    [BenchmarkCategory("1_Sync_NoRetry")]
    [Benchmark(Baseline = true)]
    public void Direct_Sync_NoRetry()
    {
        StaticSimulateWork();
    }

    [BenchmarkCategory("1_Sync_NoRetry")]
    [Benchmark]
    public void Foundatio_Sync_NoRetry()
    {
        _foundatioNoRetry.Execute(static _ => StaticSimulateWork(), CancellationToken.None);
    }

    [BenchmarkCategory("1_Sync_NoRetry")]
    [Benchmark]
    public void Polly_Sync_NoRetry()
    {
        _pollyNoRetry.Execute(static _ => StaticSimulateWork(), CancellationToken.None);
    }

    // ============================================================
    // SCENARIO 2: Sync - With Retries
    // Measures overhead when retry policy is configured (sync)
    // ============================================================

    [BenchmarkCategory("2_Sync_WithRetry")]
    [Benchmark(Baseline = true)]
    public void Direct_Sync_WithRetry()
    {
        StaticSimulateWork();
    }

    [BenchmarkCategory("2_Sync_WithRetry")]
    [Benchmark]
    public void Foundatio_Sync_WithRetry()
    {
        _foundatioWithRetry.Execute(static _ => StaticSimulateWork(), CancellationToken.None);
    }

    [BenchmarkCategory("2_Sync_WithRetry")]
    [Benchmark]
    public void Polly_Sync_WithRetry()
    {
        _pollyWithRetry.Execute(static _ => StaticSimulateWork(), CancellationToken.None);
    }

    // ============================================================
    // SCENARIO 3: Sync - With Result
    // Measures overhead for sync operations returning a result
    // ============================================================

    [BenchmarkCategory("3_Sync_WithResult")]
    [Benchmark(Baseline = true)]
    public int Direct_Sync_WithResult()
    {
        return StaticSimulateWorkWithResult();
    }

    [BenchmarkCategory("3_Sync_WithResult")]
    [Benchmark]
    public int Foundatio_Sync_WithResult()
    {
        return _foundatioWithRetry.Execute(static _ => StaticSimulateWorkWithResult(), CancellationToken.None);
    }

    [BenchmarkCategory("3_Sync_WithResult")]
    [Benchmark]
    public int Polly_Sync_WithResult()
    {
        return _pollyWithRetry.Execute(static _ => StaticSimulateWorkWithResult(), CancellationToken.None);
    }

    // ============================================================
    // SCENARIO 4: Async - No Retries
    // Measures pure framework overhead without retry configuration
    // ============================================================

    [BenchmarkCategory("4_Async_NoRetry")]
    [Benchmark(Baseline = true)]
    public ValueTask Direct_Async_NoRetry()
    {
        return SimulateWorkAsync();
    }

    [BenchmarkCategory("4_Async_NoRetry")]
    [Benchmark]
    public ValueTask Foundatio_Async_NoRetry()
    {
        return _foundatioNoRetry.ExecuteAsync(_ => SimulateWorkAsync(), CancellationToken.None);
    }

    [BenchmarkCategory("4_Async_NoRetry")]
    [Benchmark]
    public ValueTask Polly_Async_NoRetry()
    {
        return _pollyNoRetry.ExecuteAsync(_ => SimulateWorkAsync(), CancellationToken.None);
    }

    // ============================================================
    // SCENARIO 5: Async - With Retries
    // Measures overhead when retry policy is configured (but not triggered)
    // ============================================================

    [BenchmarkCategory("5_Async_WithRetry")]
    [Benchmark(Baseline = true)]
    public ValueTask Direct_Async_WithRetry()
    {
        return SimulateWorkAsync();
    }

    [BenchmarkCategory("5_Async_WithRetry")]
    [Benchmark]
    public ValueTask Foundatio_Async_WithRetry()
    {
        return _foundatioWithRetry.ExecuteAsync(_ => SimulateWorkAsync(), CancellationToken.None);
    }

    [BenchmarkCategory("5_Async_WithRetry")]
    [Benchmark]
    public ValueTask Polly_Async_WithRetry()
    {
        return _pollyWithRetry.ExecuteAsync(_ => SimulateWorkAsync(), CancellationToken.None);
    }

    // ============================================================
    // SCENARIO 6: Async - With Result
    // Measures framework overhead when returning values
    // ============================================================

    [BenchmarkCategory("6_Async_WithResult")]
    [Benchmark(Baseline = true)]
    public ValueTask<int> Direct_Async_WithResult()
    {
        return SimulateWorkWithResultAsync();
    }

    [BenchmarkCategory("6_Async_WithResult")]
    [Benchmark]
    public ValueTask<int> Foundatio_Async_WithResult()
    {
        return _foundatioWithRetry.ExecuteAsync(_ => SimulateWorkWithResultAsync(), CancellationToken.None);
    }

    [BenchmarkCategory("6_Async_WithResult")]
    [Benchmark]
    public ValueTask<int> Polly_Async_WithResult()
    {
        return _pollyWithRetryWithResult.ExecuteAsync(_ => SimulateWorkWithResultAsync(), CancellationToken.None);
    }

    // ============================================================
    // SCENARIO 7: Zero Allocation - Static Lambda (Async)
    // Tests if static lambdas eliminate delegate allocations
    // ============================================================

    [BenchmarkCategory("7_ZeroAlloc_Static")]
    [Benchmark(Baseline = true)]
    public ValueTask Direct_ZeroAlloc_Static()
    {
        return StaticSimulateWorkAsync();
    }

    [BenchmarkCategory("7_ZeroAlloc_Static")]
    [Benchmark]
    public ValueTask Foundatio_ZeroAlloc_Static()
    {
        return _foundatioWithRetry.ExecuteAsync(static _ => StaticSimulateWorkAsync(), CancellationToken.None);
    }

    [BenchmarkCategory("7_ZeroAlloc_Static")]
    [Benchmark]
    public ValueTask Polly_ZeroAlloc_Static()
    {
        return _pollyWithRetry.ExecuteAsync(static _ => StaticSimulateWorkAsync(), CancellationToken.None);
    }

    // ============================================================
    // SCENARIO 8: Zero Allocation - State-Based API (Async)
    // Tests state-based overloads for zero allocation with instance data
    // ============================================================

    [BenchmarkCategory("8_ZeroAlloc_State")]
    [Benchmark(Baseline = true)]
    public ValueTask Direct_ZeroAlloc_State()
    {
        return StaticSimulateWorkWithStateAsync(_counter);
    }

    [BenchmarkCategory("8_ZeroAlloc_State")]
    [Benchmark]
    public ValueTask Foundatio_ZeroAlloc_State()
    {
        return _foundatioWithRetry.ExecuteAsync(_counter, static (state, _) => StaticSimulateWorkWithStateAsync(state), CancellationToken.None);
    }

    [BenchmarkCategory("8_ZeroAlloc_State")]
    [Benchmark]
    public ValueTask Polly_ZeroAlloc_State()
    {
        // Polly doesn't have a state-based overload, so this will allocate a closure
        var counter = _counter;
        return _pollyWithRetry.ExecuteAsync(_ => StaticSimulateWorkWithStateAsync(counter), CancellationToken.None);
    }

    // ============================================================
    // SIMULATED OPERATIONS
    // Minimal work to measure framework overhead, not operation cost
    // ============================================================

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StaticSimulateWork()
    {
        // No-op for measuring pure overhead
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int StaticSimulateWorkWithResult()
    {
        return 42;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask StaticSimulateWorkAsync()
    {
        return ValueTask.CompletedTask;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask StaticSimulateWorkWithStateAsync(int state)
    {
        return ValueTask.CompletedTask;
    }

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

