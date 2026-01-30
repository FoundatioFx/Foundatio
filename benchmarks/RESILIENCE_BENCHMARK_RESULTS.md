# ResiliencePolicy vs Polly Performance Benchmarks

This benchmark compares the performance overhead of Foundatio's `IResiliencePolicy` vs Polly's `ResiliencePipeline` when executing operations that always succeed (measuring pure framework overhead, not retry behavior).

## Results Summary

### Synchronous Execution

| Method                     | Category           | Mean      | Allocated |
| -------------------------- | ------------------ | --------- | --------- |
| Direct_Sync_NoRetry        | 1_Sync_NoRetry     | 0.28 ns   | -         |
| Foundatio_Sync_NoRetry     | 1_Sync_NoRetry     | 23.29 ns  | -         |
| Polly_Sync_NoRetry         | 1_Sync_NoRetry     | 19.92 ns  | -         |
|                            |                    |           |           |
| Direct_Sync_WithRetry      | 2_Sync_WithRetry   | 0.18 ns   | -         |
| Foundatio_Sync_WithRetry   | 2_Sync_WithRetry   | 23.01 ns  | -         |
| Polly_Sync_WithRetry       | 2_Sync_WithRetry   | 121.87 ns | -         |
|                            |                    |           |           |
| Direct_Sync_WithResult     | 3_Sync_WithResult  | 0.26 ns   | -         |
| Foundatio_Sync_WithResult  | 3_Sync_WithResult  | 25.11 ns  | -         |
| Polly_Sync_WithResult      | 3_Sync_WithResult  | 126.83 ns | 24 B      |

### Asynchronous Execution

| Method                     | Category           | Mean      | Allocated |
| -------------------------- | ------------------ | --------- | --------- |
| Direct_Async_NoRetry       | 4_Async_NoRetry    | 2.80 ns   | -         |
| Foundatio_Async_NoRetry    | 4_Async_NoRetry    | 36.39 ns  | 64 B      |
| Polly_Async_NoRetry        | 4_Async_NoRetry    | 36.61 ns  | 64 B      |
|                            |                    |           |           |
| Direct_Async_WithRetry     | 5_Async_WithRetry  | 2.83 ns   | -         |
| Foundatio_Async_WithRetry  | 5_Async_WithRetry  | 36.86 ns  | 64 B      |
| Polly_Async_WithRetry      | 5_Async_WithRetry  | 141.24 ns | 64 B      |
|                            |                    |           |           |
| Direct_Async_WithResult    | 6_Async_WithResult | 3.11 ns   | -         |
| Foundatio_Async_WithResult | 6_Async_WithResult | 38.65 ns  | 64 B      |
| Polly_Async_WithResult     | 6_Async_WithResult | 115.35 ns | 64 B      |

### Zero-Allocation Patterns

| Method                     | Category           | Mean      | Allocated |
| -------------------------- | ------------------ | --------- | --------- |
| Direct_ZeroAlloc_Static    | 7_ZeroAlloc_Static | 1.16 ns   | -         |
| Foundatio_ZeroAlloc_Static | 7_ZeroAlloc_Static | 29.21 ns  | -         |
| Polly_ZeroAlloc_Static     | 7_ZeroAlloc_Static | 133.70 ns | -         |
|                            |                    |           |           |
| Direct_ZeroAlloc_State     | 8_ZeroAlloc_State  | 1.04 ns   | -         |
| Foundatio_ZeroAlloc_State  | 8_ZeroAlloc_State  | 30.71 ns  | -         |
| Polly_ZeroAlloc_State      | 8_ZeroAlloc_State  | 130.71 ns | 88 B      |

## Key Findings

### Sync Performance

| Scenario        | Foundatio | Polly     | Foundatio Advantage   |
| --------------- | --------- | --------- | --------------------- |
| No retry        | 23.3 ns   | 19.9 ns   | Polly ~17% faster     |
| With retry      | 23.0 ns   | 121.9 ns  | **5.3x faster**       |
| With result     | 25.1 ns   | 126.8 ns  | **5.1x faster**       |

Sync execution in Foundatio allocates **0 bytes** across all scenarios. Polly allocates 24 bytes when returning results.

### Async Performance

| Scenario        | Foundatio | Polly     | Foundatio Advantage   |
| --------------- | --------- | --------- | --------------------- |
| No retry        | 36.4 ns   | 36.6 ns   | ~equal                |
| With retry      | 36.9 ns   | 141.2 ns  | **3.8x faster**       |
| With result     | 38.7 ns   | 115.3 ns  | **3.0x faster**       |

Both libraries allocate 64 bytes for async execution (async state machine overhead).

### Zero-Allocation Performance

For performance-critical paths, Foundatio's state-based overloads achieve **zero heap allocations**:

| Pattern              | Foundatio        | Polly           | Foundatio Advantage               |
| -------------------- | ---------------- | --------------- | --------------------------------- |
| Static lambda        | 29.2 ns, **0 B** | 133.7 ns, 0 B   | **4.6x faster**                   |
| State-based          | 30.7 ns, **0 B** | 130.7 ns, 88 B  | **4.3x faster, zero allocations** |

Polly still allocates 88 bytes even with the state-based pattern, while Foundatio achieves true zero-allocation execution.

## Why Foundatio is Faster

**Foundatio's architecture:**

- Simple `do-while` loop with direct property checks
- Null-conditional operators for optional features (`CircuitBreaker?.BeforeCall()`)
- Direct field access with no indirection layers
- When `Timeout <= 0`, no `CancellationTokenSource` allocations
- Dedicated sync `Execute` methods (no async overhead)
- State-based overloads avoid closure allocations entirely

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
// No Retry: 1 attempt, no delay
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
// No Retry: Empty pipeline
_pollyNoRetry = new ResiliencePipelineBuilder().Build();

// With Retry: 2 retries (3 total attempts)
_pollyWithRetry = new ResiliencePipelineBuilder()
    .AddRetry(new RetryStrategyOptions
    {
        MaxRetryAttempts = 2,
        Delay = TimeSpan.Zero
    })
    .Build();
```

### Zero-Allocation Examples

```csharp
// Foundatio state-based (0 B allocations)
_foundatioWithRetry.ExecuteAsync(this, static (state, ct) =>
{
    return state.SimulateWorkAsync();
});

// Polly state-based (88 B allocations)
_pollyWithRetry.ExecuteAsync(
    static (state, ct) => state.SimulateWorkAsync(),
    this);
```

## Recommendations

### When to use Foundatio ResiliencePolicy

1. **Production workloads with retry policies**: 3-5x faster than Polly when retries are configured
2. **Zero-allocation requirements**: State-based overloads achieve true zero-allocation execution
3. **Sync execution**: Native sync methods without async overhead
4. **Consistent performance**: Overhead is predictable (~23-39ns) regardless of configuration
5. **High-frequency operations**: Lower overhead matters when called millions of times

### When to use Polly

1. **Complex resilience patterns**: Bulkhead isolation, hedging, rate limiting, or advanced retry strategies
2. **Pass-through scenarios**: Slightly faster when no retry logic is needed
3. **Ecosystem integration**: Better tooling, documentation, and community support

## Test Environment

- .NET 10.0.2
- AMD Ryzen 7 9800X3D, 1 CPU, 16 logical / 8 physical cores
- Windows 11
- BenchmarkDotNet v0.15.8
