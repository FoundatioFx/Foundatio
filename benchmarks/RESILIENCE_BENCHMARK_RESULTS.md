# ResiliencePolicy vs Polly Performance Benchmarks

This benchmark compares the performance overhead of using Foundatio's `IResiliencePolicy` vs Microsoft's Polly library when calling methods directly with no exceptions and no retries.

## Results Summary

| Benchmark | Mean | Overhead vs Direct Call | Allocated Memory |
|-----------|------|------------------------|------------------|
| **DirectCall_Async** | 465.5 ns | Baseline | 213 B |
| **ResiliencePolicy_NoRetries_Async** | 598.4 ns | **+28.5% (+132.9 ns)** | 810 B (+3.8x) |
| **ResiliencePolicy_StandardConfig_Async** | 598.8 ns | **+28.6% (+133.3 ns)** | 811 B (+3.8x) |
| **Polly_NoRetries_Async** | 607.6 ns | **+30.5% (+142.1 ns)** | 686 B (+3.2x) |
| **Polly_StandardConfig_Async** | 1,089.8 ns | **+134.0% (+624.3 ns)** | 1,403 B (+6.6x) |
| **DirectCall_ComputeIntensive_Async** | 690.9 ns | Baseline | 96 B |
| **ResiliencePolicy_ComputeIntensive_Async** | 817.2 ns | **+18.3% (+126.3 ns)** | 744 B (+7.8x) |
| **Polly_ComputeIntensive_Async** | 794.9 ns | **+15.1% (+104.0 ns)** | 504 B (+5.3x) |
| **DirectCall_Sync** | 2.97 ns | Baseline | 0 B |
| **ResiliencePolicy_NoRetries_WithResult_Async** | 39.3 ns | **+1,225% (+36.4 ns)** | 136 B |
| **ResiliencePolicy_StandardConfig_WithResult_Async** | 39.4 ns | **+1,227% (+36.4 ns)** | 136 B |
| **Polly_NoRetries_WithResult_Async** | 39.0 ns | **+1,213% (+36.0 ns)** | 136 B |
| **Polly_StandardConfig_WithResult_Async** | 133.3 ns | **+4,390% (+130.3 ns)** | 136 B |

## Key Findings

### Performance Comparison: Foundatio vs Polly

**For basic async operations (~466 ns baseline):**

- **Foundatio ResiliencePolicy**: ~28.5% overhead (132-133 ns)
- **Polly (no retries)**: ~30.5% overhead (142 ns) - **7% slower than Foundatio**
- **Polly (with retries configured)**: ~134% overhead (624 ns) - **82% slower than Foundatio**

**For compute-intensive operations (~691 ns baseline):**

- **Foundatio ResiliencePolicy**: ~18.3% overhead (126 ns)
- **Polly**: ~15.1% overhead (104 ns) - **18% faster than Foundatio**

**For sync-to-async wrapping (very fast operations):**

- **Foundatio**: ~39 ns overhead (consistent across configurations)
- **Polly (no retries)**: ~39 ns overhead (equivalent to Foundatio)
- **Polly (with retries configured)**: ~133 ns overhead - **241% slower than Foundatio**

### Memory Allocation Comparison

- **Foundatio**: 3.8x more allocations for async operations (213 B → 810 B)
- **Polly (no retries)**: 3.2x more allocations (213 B → 686 B) - **18% less memory than Foundatio**
- **Polly (with retries)**: 6.6x more allocations (213 B → 1,403 B) - **73% more memory than Foundatio**

### Configuration Impact

**Foundatio:**

- **No significant difference** between minimal configuration (MaxAttempts=1) and standard configuration (MaxAttempts=3, Delay=100ms) when no retries are needed
- Both configurations perform nearly identically for successful operations

**Polly:**

- **Significant performance impact** when retry policies are configured, even when not triggered
- Standard configuration shows ~82% performance degradation compared to no-retry configuration
- Memory allocations nearly double with retry configuration

## Recommendations

### When to use Foundatio ResiliencePolicy

1. **Consistent performance requirements**: When you need predictable overhead regardless of configuration complexity
2. **Moderate retry scenarios**: For operations that occasionally need 2-3 retries with simple backoff
3. **Memory-conscious applications**: When allocation overhead is acceptable for consistency

### When to use Polly

1. **High-performance, no-retry scenarios**: For simple circuit breaker patterns without retry logic
2. **Compute-intensive operations**: When the base operation time is already significant (>500ns)
3. **Complex resilience patterns**: When you need advanced features like bulkhead isolation, hedging, or complex retry strategies

### General Guidelines

1. **For high-frequency, fast operations**: Both libraries add significant relative overhead (~30-35%)
2. **For compute-intensive operations**: Overhead becomes less significant relative to actual work
3. **Configuration matters**: Polly shows significant performance degradation with complex policies, while Foundatio maintains consistent performance

## Test Environment

- .NET 8.0.17 (8.0.1725.26602)
- X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
- Windows 11 (10.0.26100.4484/24H2/2024Update/HudsonValley)
- BenchmarkDotNet v0.15.2
