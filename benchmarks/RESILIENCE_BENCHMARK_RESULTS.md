# ResiliencePolicy vs Polly Performance Benchmarks

This benchmark compares the performance overhead of Foundatio's `IResiliencePolicy` vs Polly's `ResiliencePipeline` when executing operations that always succeed (measuring pure framework overhead, not retry behavior).

## Results Summary

| Method | Category | Mean | Ratio | Allocated |
|--------|----------|------|-------|-----------|
| Direct_NoRetry | 1_NoRetry | 2.84 ns | 1.00 | - |
| Foundatio_NoRetry | 1_NoRetry | 33.59 ns | 11.84x | 64 B |
| Polly_NoRetry | 1_NoRetry | 32.57 ns | 11.48x | 64 B |
| | | | | |
| Direct_WithRetry | 2_WithRetry | 2.84 ns | 1.00 | - |
| Foundatio_WithRetry | 2_WithRetry | 33.62 ns | 11.84x | 64 B |
| Polly_WithRetry | 2_WithRetry | 137.44 ns | 48.41x | 64 B |
| | | | | |
| Direct_NoRetry_WithResult | 3_NoRetry_WithResult | 3.09 ns | 1.00 | - |
| Foundatio_NoRetry_WithResult | 3_NoRetry_WithResult | 35.46 ns | 11.47x | 64 B |
| Polly_NoRetry_WithResult | 3_NoRetry_WithResult | 32.79 ns | 10.61x | 64 B |
| | | | | |
| Direct_WithRetry_WithResult | 4_WithRetry_WithResult | 3.10 ns | 1.00 | - |
| Foundatio_WithRetry_WithResult | 4_WithRetry_WithResult | 36.34 ns | 11.72x | 64 B |
| Polly_WithRetry_WithResult | 4_WithRetry_WithResult | 104.98 ns | 33.87x | 64 B |

## Key Findings

### No Retry Configured (Base Framework Overhead)

When no retry policy is configured, both libraries perform nearly identically:

| Scenario | Foundatio | Polly | Difference |
|----------|-----------|-------|------------|
| Async (void) | 33.6 ns | 32.6 ns | Polly ~3% faster |
| With Result | 35.5 ns | 32.8 ns | Polly ~8% faster |

**Conclusion**: For pass-through scenarios with no retry logic, Polly's empty pipeline has a slight edge.

### With Retry Configured (Real-World Scenario)

When retry policies are configured (3 attempts, even though no retries occur), Foundatio is significantly faster:

| Scenario | Foundatio | Polly | Foundatio Advantage |
|----------|-----------|-------|---------------------|
| Async (void) | 33.6 ns | 137.4 ns | **4.1x faster** |
| With Result | 36.3 ns | 105.0 ns | **2.9x faster** |

**Conclusion**: Foundatio maintains consistent ~34-36ns overhead regardless of configuration, while Polly's overhead increases 3-4x when retry strategies are added.

### Memory Allocations

Both libraries allocate **64 bytes** per operation when using resilience policies. The direct call allocates nothing.

### Why Foundatio is Faster with Retry Configuration

**Foundatio's architecture:**
- Simple `do-while` loop with direct property checks
- Null-conditional operators for optional features (`CircuitBreaker?.BeforeCall()`)
- Direct field access with no indirection layers
- When `Timeout <= 0`, no `CancellationTokenSource` allocations
- On success path: calls action, checks circuit breaker (null), returns

**Polly's architecture:**
- Pipeline-based with strategy composition
- Each strategy (retry, circuit breaker, timeout) is a separate component in a chain
- `ResiliencePipeline.ExecuteAsync` iterates through the strategy stack
- `RetryResilienceStrategy` has its own state machine for tracking attempts
- Uses `ResilienceContext` pooling and complex continuation logic

The flexibility of Polly's composable pipeline architecture comes at a performance cost when strategies are configured.

## Benchmark Configuration

### Foundatio Setup

```csharp
// No Retry: 1 attempt, no delay (measures base framework overhead)
_foundatioNoRetry = new ResiliencePolicyBuilder()
    .WithMaxAttempts(1)
    .WithDelay(TimeSpan.Zero)
    .Build();

// With Retry: 3 attempts total
_foundatioWithRetry = new ResiliencePolicyBuilder()
    .WithMaxAttempts(3)
    .WithDelay(TimeSpan.Zero)
    .Build();
```

### Polly Setup

```csharp
// No Retry: Empty pipeline (no retry strategy)
_pollyNoRetry = new ResiliencePipelineBuilder()
    .Build();

// With Retry: 2 retries (3 total attempts)
_pollyWithRetry = new ResiliencePipelineBuilder()
    .AddRetry(new RetryStrategyOptions
    {
        MaxRetryAttempts = 2,
        Delay = TimeSpan.Zero
    })
    .Build();
```

### Simulated Operations

```csharp
// Minimal work to measure framework overhead, not operation cost
private ValueTask SimulateWorkAsync()
{
    Interlocked.Increment(ref _counter);
    return ValueTask.CompletedTask;
}

private ValueTask<int> SimulateWorkWithResultAsync()
{
    return ValueTask.FromResult(Interlocked.Increment(ref _counter));
}
```

## Recommendations

### When to use Foundatio ResiliencePolicy

1. **Production workloads with retry policies**: 3-4x faster than Polly when retries are configured
2. **Consistent performance requirements**: Overhead is predictable (~34-36ns) regardless of configuration
3. **High-frequency operations**: Lower overhead matters when called millions of times

### When to use Polly

1. **Complex resilience patterns**: Bulkhead isolation, hedging, rate limiting, or advanced retry strategies
2. **Pass-through scenarios**: Slightly faster when no retry logic is needed
3. **Ecosystem integration**: Better tooling, documentation, and community support

## Test Environment

- .NET 10.0.2
- AMD Ryzen 7 9800X3D, 1 CPU, 16 logical / 8 physical cores
- Windows 11
- BenchmarkDotNet v0.15.8
