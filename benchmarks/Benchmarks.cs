using System;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Foundatio.Resilience;
using Polly;
using Polly.Retry;

namespace Foundatio.Benchmarks;

[MemoryDiagnoser]
[SimpleJob]
public class Benchmarks
{
    private IResiliencePolicy _policy;
    private IResiliencePolicy _minimalPolicy;
    private ResiliencePipeline _pollyMinimalPipeline;
    private ResiliencePipeline _pollyStandardPipeline;
    private ResiliencePipeline<int> _pollyMinimalPipelineWithResult;
    private ResiliencePipeline<int> _pollyStandardPipelineWithResult;
    private int _counter;

    [GlobalSetup]
    public void Setup()
    {
        // Standard policy with typical settings
        _policy = new ResiliencePolicyBuilder().WithMaxAttempts(3).WithDelay(TimeSpan.FromMilliseconds(100)).Build();

        // Minimal policy with just 1 attempt (no retries) to measure base overhead
        _minimalPolicy = new ResiliencePolicyBuilder().WithMaxAttempts(1).WithDelay(TimeSpan.Zero).Build();

        // Polly minimal pipeline - no retries (equivalent to _minimalPolicy)
        _pollyMinimalPipeline = new ResiliencePipelineBuilder()
            .Build();

        // Polly standard pipeline with retries (equivalent to _policy)
        _pollyStandardPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2, // Total attempts = 3 (1 + 2 retries)
                Delay = TimeSpan.FromMilliseconds(100)
            })
            .Build();

        // Polly pipelines with result types
        _pollyMinimalPipelineWithResult = new ResiliencePipelineBuilder<int>()
            .Build();

        _pollyStandardPipelineWithResult = new ResiliencePipelineBuilder<int>()
            .AddRetry(new RetryStrategyOptions<int>
            {
                MaxRetryAttempts = 2, // Total attempts = 3 (1 + 2 retries)
                Delay = TimeSpan.FromMilliseconds(100)
            })
            .Build();
    }

    [Benchmark(Baseline = true)]
    public async Task DirectCall_Async()
    {
        await SimulateSuccessfulOperation();
    }

    [Benchmark]
    public async Task ResiliencePolicy_NoRetries_Async()
    {
        await _minimalPolicy.ExecuteAsync(_ => SimulateSuccessfulOperation(), CancellationToken.None);
    }

    [Benchmark]
    public async Task ResiliencePolicy_StandardConfig_Async()
    {
        await _policy.ExecuteAsync(_ => SimulateSuccessfulOperation(), CancellationToken.None);
    }

    [Benchmark]
    public int DirectCall_Sync()
    {
        return SimulateSuccessfulOperation_Sync();
    }

    [Benchmark]
    public async Task<int> ResiliencePolicy_NoRetries_WithResult_Async()
    {
        return await _minimalPolicy.ExecuteAsync(_ => ValueTask.FromResult(SimulateSuccessfulOperation_Sync()), CancellationToken.None);
    }

    [Benchmark]
    public async Task<int> ResiliencePolicy_StandardConfig_WithResult_Async()
    {
        return await _policy.ExecuteAsync(_ => ValueTask.FromResult(SimulateSuccessfulOperation_Sync()), CancellationToken.None);
    }

    [Benchmark]
    public async Task DirectCall_ComputeIntensive_Async()
    {
        await Task.Yield();
        // Simulate some CPU work
        var result = 0;
        for (int i = 0; i < 1000; i++)
        {
            result += i * 2;
        }
    }

    [Benchmark]
    public async Task ResiliencePolicy_ComputeIntensive_Async()
    {
        await _minimalPolicy.ExecuteAsync(async _ =>
        {
            await Task.Yield();
            // Simulate some CPU work
            var result = 0;
            for (int i = 0; i < 1000; i++)
            {
                result += i * 2;
            }
        }, CancellationToken.None);
    }

    // Polly equivalent benchmarks
    [Benchmark]
    public async Task Polly_NoRetries_Async()
    {
        await _pollyMinimalPipeline.ExecuteAsync(async _ => await SimulateSuccessfulOperation(), CancellationToken.None);
    }

    [Benchmark]
    public async Task Polly_StandardConfig_Async()
    {
        await _pollyStandardPipeline.ExecuteAsync(async _ => await SimulateSuccessfulOperation(), CancellationToken.None);
    }

    [Benchmark]
    public async Task<int> Polly_NoRetries_WithResult_Async()
    {
        return await _pollyMinimalPipelineWithResult.ExecuteAsync(_ => ValueTask.FromResult(SimulateSuccessfulOperation_Sync()), CancellationToken.None);
    }

    [Benchmark]
    public async Task<int> Polly_StandardConfig_WithResult_Async()
    {
        return await _pollyStandardPipelineWithResult.ExecuteAsync(_ => ValueTask.FromResult(SimulateSuccessfulOperation_Sync()), CancellationToken.None);
    }

    [Benchmark]
    public async Task Polly_ComputeIntensive_Async()
    {
        await _pollyMinimalPipeline.ExecuteAsync(async ct =>
        {
            await Task.Yield();
            // Simulate some CPU work
            var result = 0;
            for (int i = 0; i < 1000; i++)
            {
                result += i * 2;
            }
        }, CancellationToken.None);
    }

    private async ValueTask SimulateSuccessfulOperation()
    {
        // Simulate a quick async operation that always succeeds
        await Task.Yield();
        Interlocked.Increment(ref _counter);
    }

    private int SimulateSuccessfulOperation_Sync()
    {
        // Simulate a quick sync operation that always succeeds
        return Interlocked.Increment(ref _counter);
    }
}
