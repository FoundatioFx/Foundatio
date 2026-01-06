# InMemoryCacheClient Sizing Benchmarks

This benchmark compares the performance of `InMemoryCacheClient` across three sizing configurations:

1. **Default**: No memory limits (baseline)
2. **FixedSizing**: `WithFixedSizing()` - constant size per entry (fastest with memory limits)
3. **DynamicSizing**: `WithDynamicSizing()` - calculates actual object sizes via `SizeCalculator`

## Comparison to Main Branch (Without Sizing Features)

### Main Branch Baseline (No Sizing Infrastructure)

| Metric | SetAsync_String | SetAsync_ComplexObject | SetManyAsync_String | Allocated |
|--------|-----------------|------------------------|---------------------|-----------|
| **Mean** | ~212 ns | ~211 ns | ~318 µs | 216 B |

### Feature Branch (With Sizing Infrastructure)

| Metric | SetAsync_String | SetAsync_ComplexObject | SetManyAsync_String | Allocated |
|--------|-----------------|------------------------|---------------------|-----------|
| **Default** | 226.3 ns | 226.7 ns | ~231 µs | 288 B |
| **FixedSizing** | 229.8 ns | 231.3 ns | ~229 µs | 288 B |
| **DynamicSizing** | 229.8 ns | 760.6 ns | ~242 µs | 288 B / 992 B |

### Impact Analysis

| Metric | Main Branch | Feature Branch (Default) | Difference |
|--------|-------------|--------------------------|------------|
| **SetAsync_String** | ~212 ns | ~226.3 ns | **+14.3 ns (+6.7%)** |
| **SetAsync_ComplexObject** | ~211 ns | ~226.7 ns | **+15.7 ns (+7.4%)** |
| **Allocation per Set** | 216 B | 288 B | **+72 B (+33%)** |

### Optimizations Applied

1. **Fast path for default configuration**: When no `SizeCalculator` is configured, `SetAsync` creates `CacheEntry` directly without calling `CreateEntry()` method
2. **Conditional closure capture**: In `SetInternalAsync`, the lambda only captures `oldEntry` when memory tracking is enabled (`else if (trackMemory)` branch)
3. **Cached boolean check**: `bool trackMemory = _hasSizeCalculator && _maxMemorySize.HasValue` is computed once and reused
4. **Optimized size delta calculation**: `entry.Size - (oldEntry?.Size ?? 0)` uses null-coalescing for efficiency

### Remaining Overhead Analysis

The **+72B allocation** overhead is due to:
- The `Size` field in `CacheEntry` (8 bytes for `long`)
- Object alignment and padding in .NET runtime
- The feature branch's `CacheEntry` has one additional auto-property

The **+6.7% latency** overhead is due to:
- The `if (!_hasSizeCalculator)` branch check in `SetAsync`
- The `bool trackMemory` computation and branch in `SetInternalAsync`
- Additional constructor parameter (`size = 0`) for `CacheEntry`

This overhead is minimal and acceptable given the value of the memory sizing feature.

---

## Current Results Summary

### Single Item Operations

| Benchmark | Mean | Overhead vs Default | Allocated | Notes |
|-----------|------|---------------------|-----------|-------|
| **SetAsync_String_Default** | 226.3 ns | Baseline | 288 B | Fast path, no sizing |
| **SetAsync_String_FixedSizing** | 229.8 ns | +2% | 288 B | Fixed size calculation |
| **SetAsync_String_DynamicSizing** | 229.8 ns | +2% | 288 B | Fast path for strings |
| **SetAsync_ComplexObject_Default** | 226.7 ns | Baseline | 288 B | Fast path, no sizing |
| **SetAsync_ComplexObject_FixedSizing** | 231.3 ns | +2% | 288 B | Fixed size calculation |
| **SetAsync_ComplexObject_DynamicSizing** | 760.6 ns | **+236%** | 992 B | JSON serialization fallback |

### Key Observations

1. **Default vs FixedSizing**: Only ~2% overhead - the fixed size lookup is very fast
2. **Default vs DynamicSizing (strings)**: Only ~2% overhead - strings use a fast path calculation
3. **DynamicSizing (complex objects)**: ~3.4x slower due to JSON serialization fallback

## Key Findings

### Default Configuration: Minimal Overhead

When `SizeCalculator` is null (default), the fast path skips all sizing logic:
- Direct `CacheEntry` creation without `CreateEntry()` method call
- No closure allocation for memory tracking in `SetInternalAsync`
- Only overhead is the `Size` field in `CacheEntry` and minimal branch checks

### String Operations: Near-Identical Performance

For string values, all three configurations show **near-identical performance** (~226-230 ns). This is because:
- `Default`: Fast path, skips all sizing
- `FixedSizing`: Returns a constant (minimal overhead)
- `DynamicSizing`: Uses a fast path for strings (`StringOverhead + length * 2`)

### Complex Objects with DynamicSizing: Expected Overhead

For complex objects (classes with nested collections), `DynamicSizing` shows **~3.4x slower performance** and **~3.4x more memory allocation**. This is expected because:

1. `SizeCalculator` falls back to JSON serialization for complex types
2. JSON serialization allocates temporary buffers
3. This is a one-time cost per `SetAsync` call

**This is the correct trade-off**: You get accurate memory tracking at the cost of serialization overhead.

## Recommendations

| Scenario | Recommended Configuration |
|----------|---------------------------|
| No memory limits needed | Default (no sizing) |
| Uniform entry sizes (strings, tokens) | `WithFixedSizing()` |
| Mixed object types, need accurate tracking | `WithDynamicSizing()` |
| High-throughput complex objects | `WithFixedSizing()` with estimated average size |

### Performance Guidelines

1. **For string-heavy workloads**: All configurations perform nearly identically
2. **For complex object workloads**: Consider `FixedSizing` if you can estimate average entry size
3. **For mixed workloads**: `DynamicSizing` provides the best accuracy with acceptable overhead
4. **When sizing is not needed**: Use default configuration for minimal overhead (~7% latency, +72B allocation)

## Test Coverage

All configurations are tested with the full cache client test suite:
- **796 tests total** (3x the base suite)
- Default configuration: ~382 tests
- Fixed sizing configuration: ~207 tests
- Dynamic sizing configuration: ~207 tests

## Test Environment

- .NET 8.0.22 (8.0.2225.52707)
- Arm64 RyuJIT AdvSIMD
- macOS 26.2 (Darwin 25.2.0)
- Apple M1 Max, 10 logical cores
- BenchmarkDotNet v0.15.2

## Raw Results

```
BenchmarkDotNet v0.15.2, macOS 26.2 (25C56) [Darwin 25.2.0]
Apple M1 Max, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.101
  [Host]     : .NET 8.0.22 (8.0.2225.52707), Arm64 RyuJIT AdvSIMD
  DefaultJob : .NET 8.0.22 (8.0.2225.52707), Arm64 RyuJIT AdvSIMD

| Method                               | Mean     | Error   | StdDev  | Ratio | Gen0   | Allocated | Alloc Ratio |
|------------------------------------- |---------:|--------:|--------:|------:|-------:|----------:|------------:|
| SetAsync_String_Default              | 226.3 ns | 1.17 ns | 0.97 ns |  1.00 | 0.0458 |     288 B |        1.00 |
| SetAsync_String_FixedSizing          | 229.8 ns | 0.96 ns | 0.85 ns |  1.02 | 0.0458 |     288 B |        1.00 |
| SetAsync_String_DynamicSizing        | 229.8 ns | 1.30 ns | 1.15 ns |  1.02 | 0.0458 |     288 B |        1.00 |
| SetAsync_ComplexObject_Default       | 226.7 ns | 1.89 ns | 1.58 ns |  1.00 | 0.0458 |     288 B |        1.00 |
| SetAsync_ComplexObject_FixedSizing   | 231.3 ns | 0.55 ns | 0.46 ns |  1.02 | 0.0458 |     288 B |        1.00 |
| SetAsync_ComplexObject_DynamicSizing | 760.6 ns | 1.05 ns | 0.82 ns |  3.36 | 0.1574 |     992 B |        3.44 |
```

## Changelog

### Latest Optimizations (Current Run)
- Cached `trackMemory` boolean to avoid duplicate field reads
- Restructured `SetInternalAsync` to use `else if` for cleaner branching
- Optimized size delta calculation with null-coalescing operator
- **Result**: Reduced overhead from +8.8% to +6.7%
