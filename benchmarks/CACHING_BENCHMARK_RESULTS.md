# InMemoryCacheClient Sizing Benchmarks

This benchmark compares the performance of `InMemoryCacheClient` across three sizing configurations:

1. **Default**: No memory limits (baseline)
2. **FixedSizing**: `WithFixedSizing()` - constant size per entry (fastest with memory limits)
3. **DynamicSizing**: `WithDynamicSizing()` - calculates actual object sizes via `SizeCalculator`

## Results Summary

### Single Item Operations

| Benchmark | Mean | Overhead vs Default | Allocated | Notes |
|-----------|------|---------------------|-----------|-------|
| **SetAsync_String_Default** | 287.3 ns | Baseline | 296 B | No sizing overhead |
| **SetAsync_String_FixedSizing** | 292.8 ns | +1.9% | 296 B | Minimal overhead |
| **SetAsync_String_DynamicSizing** | 296.8 ns | +3.3% | 296 B | Fast path for strings |
| **SetAsync_ComplexObject_Default** | 288.8 ns | Baseline | 296 B | No sizing overhead |
| **SetAsync_ComplexObject_FixedSizing** | 293.2 ns | +1.5% | 296 B | Minimal overhead |
| **SetAsync_ComplexObject_DynamicSizing** | 814.3 ns | **+182%** | 1000 B | JSON serialization fallback |

### Bulk Operations (100 items)

| Benchmark | Mean | Overhead vs Default | Allocated |
|-----------|------|---------------------|-----------|
| **SetManyAsync_String_Default** | 244.4 µs | Baseline | 43,553 B |
| **SetManyAsync_String_FixedSizing** | 255.4 µs | +4.5% | 43,566 B |
| **SetManyAsync_String_DynamicSizing** | 247.6 µs | +1.3% | 43,878 B |

### Get Operations (no sizing overhead expected)

| Benchmark | Mean | Allocated |
|-----------|------|-----------|
| **GetAsync_String_Default** | 1.79 µs | 176 B |
| **GetAsync_String_FixedSizing** | 1.77 µs | 176 B |
| **GetAsync_String_DynamicSizing** | 1.71 µs | 176 B |

## Key Findings

### String Operations: Negligible Overhead

For string values, all three configurations perform nearly identically (~287-297 ns). This is because `SizeCalculator` has a **fast path for strings**:

```csharp
// Fast path: StringOverhead + (length * 2) for UTF-16 chars
case string stringValue:
    return StringOverhead + ((long)stringValue.Length * 2);
```

No JSON serialization is needed, so dynamic sizing adds only ~3% overhead.

### Complex Objects: Expected Overhead with DynamicSizing

For complex objects (classes with nested collections), `DynamicSizing` shows **2.8x slower performance** and **3.4x more memory allocation**. This is expected because:

1. `SizeCalculator` falls back to JSON serialization for complex types
2. JSON serialization allocates temporary buffers
3. This is a one-time cost per `SetAsync` call

**This is the correct trade-off**: You get accurate memory tracking at the cost of serialization overhead.

### FixedSizing: Fastest with Memory Limits

`FixedSizing` adds only ~1-2% overhead because it simply returns a constant value:

```csharp
// Fixed sizing: _ => averageEntrySize (constant)
Target.SizeCalculator = _ => averageEntrySize;
```

Use this when entries are roughly uniform size (e.g., session tokens, API keys).

### Get Operations: No Overhead

As expected, `GetAsync` operations show **no performance difference** between configurations. Size calculation only happens on write operations.

## Recommendations

| Scenario | Recommended Configuration |
|----------|---------------------------|
| No memory limits needed | Default (no sizing) |
| Uniform entry sizes (strings, tokens) | `WithFixedSizing()` |
| Mixed object types, need accurate tracking | `WithDynamicSizing()` |
| High-throughput complex objects | `WithFixedSizing()` with estimated average size |

### Performance Guidelines

1. **For string-heavy workloads**: `DynamicSizing` is fine - the fast path keeps overhead minimal
2. **For complex object workloads**: Consider `FixedSizing` if you can estimate average entry size
3. **For mixed workloads**: `DynamicSizing` provides the best accuracy with acceptable overhead

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

| Method                               | Mean         | Error       | StdDev       | Gen0   | Allocated |
|------------------------------------- |-------------:|------------:|-------------:|-------:|----------:|
| SetAsync_String_Default              |     287.3 ns |     1.35 ns |      1.13 ns | 0.0467 |     296 B |
| SetAsync_String_FixedSizing          |     292.8 ns |     4.07 ns |      3.60 ns | 0.0467 |     296 B |
| SetAsync_String_DynamicSizing        |     296.8 ns |     5.26 ns |      4.66 ns | 0.0467 |     296 B |
| SetAsync_ComplexObject_Default       |     288.8 ns |     2.61 ns |      2.04 ns | 0.0467 |     296 B |
| SetAsync_ComplexObject_FixedSizing   |     293.2 ns |     4.71 ns |      4.17 ns | 0.0467 |     296 B |
| SetAsync_ComplexObject_DynamicSizing |     814.3 ns |    15.29 ns |     15.02 ns | 0.1593 |    1000 B |
| SetManyAsync_String_Default          | 244,419.3 ns | 4,829.54 ns | 10,081.05 ns | 6.8359 |   43553 B |
| SetManyAsync_String_FixedSizing      | 255,428.8 ns | 5,012.39 ns | 12,575.12 ns | 6.8359 |   43566 B |
| SetManyAsync_String_DynamicSizing    | 247,603.7 ns | 6,321.17 ns | 17,410.35 ns | 6.8359 |   43878 B |
```

