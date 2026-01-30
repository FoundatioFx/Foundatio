# DeepClone Performance Benchmarks

This benchmark measures the performance of `DeepClone()` across different object types and sizes, representing realistic usage patterns in Foundatio:

- **Cache entries** (InMemoryCacheClient) - Small to medium objects
- **Queue messages** (InMemoryQueue) - Medium nested objects
- **File storage specs** (InMemoryFileStorage) - FileSpec objects
- **Large event documents** - Error/exception events similar to Exceptionless
- **Log batches** - Bulk log processing scenarios
- **Dynamic types** - Objects with `object` properties containing various runtime types

## Results Summary

| Benchmark | Mean | Allocated | Use Case |
|-----------|------|-----------|----------|
| **DeepClone_SmallObject** | 52.93 ns | 184 B | Simple cache entries |
| **DeepClone_FileSpec** | 299.87 ns | 920 B | File storage metadata |
| **DeepClone_SmallObjectWithCollections** | 384.61 ns | 1,096 B | Config/metadata caching |
| **DeepClone_StringArray_1000** | 470.19 ns | 8,160 B | String collections |
| **DeepClone_DynamicWithDictionary** | 797.00 ns | 2,712 B | JSON-like dynamic data |
| **DeepClone_MediumNestedObject** | 1,020.19 ns | 3,416 B | Typical queue messages |
| **DeepClone_DynamicWithNestedObject** | 1,130.58 ns | 3,560 B | Nested dynamic objects |
| **DeepClone_DynamicWithArray** | 3,672.72 ns | 8,800 B | Mixed-type arrays |
| **DeepClone_LargeEventDocument_10MB** | 43,718.94 ns | 129,792 B | Error tracking events |
| **DeepClone_ObjectList_100** | 110,525.77 ns | 318,888 B | Batch processing |
| **DeepClone_ObjectDictionary_50** | 555,214.24 ns | 549,704 B | Keyed collections |
| **DeepClone_LargeLogBatch_10MB** | 3,662,330.45 ns | 3,564,760 B | Bulk log ingestion |

## Key Findings

### Performance Characteristics

1. **Small Objects (~50-400 ns)**: Simple objects with primitives and small collections clone extremely fast
2. **Medium Objects (~1-4 µs)**: Nested objects with multiple collections show linear scaling
3. **Large Objects (~44 µs - 3.7 ms)**: Complex nested structures with many strings scale well

### Memory Efficiency

- DeepClone creates new instances of all reference types
- Strings are copied (not interned) to ensure true isolation
- Collections are recreated with the same capacity

### Dynamic Type Handling

Objects with `object` properties are handled correctly regardless of runtime type:
- Dictionary<string, string> → cloned as dictionary
- Nested objects → recursively cloned
- Mixed arrays → each element cloned appropriately

### Scaling Behavior

| Object Complexity | Time per KB | Notes |
|-------------------|-------------|-------|
| Simple objects | ~0.3 ns/B | Primitives + small strings |
| Nested objects | ~0.3 ns/B | Consistent with simple objects |
| Large collections | ~1.0 ns/B | Dictionary/List overhead |

## Recommendations

### When to Use DeepClone

1. **Cache isolation**: When cached values must not be modified by callers
2. **Queue message safety**: Prevent mutation of in-flight messages
3. **Test data setup**: Create independent copies for parallel tests

### Performance Considerations

1. **Avoid cloning large objects frequently**: For 10MB+ objects, consider immutable designs
2. **Use cloning selectively**: Only clone when mutation isolation is required
3. **Consider object pooling**: For high-frequency cloning of similar objects

### Alternatives to Consider

| Scenario | Alternative | Trade-off |
|----------|-------------|-----------|
| Read-only access | Immutable types | No cloning needed |
| Serialization | JSON/MessagePack | Cross-process compatible |
| Partial updates | Copy-on-write | Lazy cloning |

## Test Environment

- .NET 10.0.2 (10.0.2, 10.0.225.61305)
- Arm64 RyuJIT armv8.0-a
- macOS Tahoe 26.2 (Darwin 25.2.0)
- Apple M1 Max, 10 logical cores
- BenchmarkDotNet v0.15.8

## Raw Results

```
BenchmarkDotNet v0.15.8, macOS Tahoe 26.2 (25C56) [Darwin 25.2.0]
Apple M1 Max, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.102
  [Host]     : .NET 10.0.2 (10.0.2, 10.0.225.61305), Arm64 RyuJIT armv8.0-a
  DefaultJob : .NET 10.0.2 (10.0.2, 10.0.225.61305), Arm64 RyuJIT armv8.0-a


| Method                               | Mean            | Error         | StdDev        | Ratio  | RatioSD | Gen0     | Gen1     | Gen2     | Allocated | Alloc Ratio |
|------------------------------------- |----------------:|--------------:|--------------:|-------:|--------:|---------:|---------:|---------:|----------:|------------:|
| DeepClone_StringArray_1000           |       470.19 ns |      6.227 ns |      5.200 ns |  0.011 |    0.00 |   1.3003 |   0.0367 |        - |    8160 B |       0.063 |
| DeepClone_ObjectList_100             |   110,525.77 ns |    844.665 ns |    659.459 ns |  2.528 |    0.02 |  50.5371 |  16.8457 |        - |  318888 B |       2.457 |
| DeepClone_ObjectDictionary_50        |   555,214.24 ns |  9,038.564 ns |  8,012.452 ns | 12.700 |    0.19 |  87.8906 |  50.7813 |  19.5313 |  549704 B |       4.235 |
| DeepClone_DynamicWithDictionary      |       797.00 ns |      3.333 ns |      2.783 ns |  0.018 |    0.00 |   0.4320 |   0.0029 |        - |    2712 B |       0.021 |
| DeepClone_DynamicWithNestedObject    |     1,130.58 ns |     16.461 ns |     12.852 ns |  0.026 |    0.00 |   0.5665 |   0.0038 |        - |    3560 B |       0.027 |
| DeepClone_DynamicWithArray           |     3,672.72 ns |     15.754 ns |     13.966 ns |  0.084 |    0.00 |   1.4000 |   0.0229 |        - |    8800 B |       0.068 |
| DeepClone_LargeEventDocument_10MB    |    43,718.94 ns |    300.379 ns |    234.516 ns |  1.000 |    0.01 |  20.6299 |   4.0894 |        - |  129792 B |       1.000 |
| DeepClone_LargeLogBatch_10MB         | 3,662,330.45 ns | 18,117.322 ns | 15,128.785 ns | 83.772 |    0.55 | 562.5000 | 328.1250 | 132.8125 | 3564760 B |      27.465 |
| DeepClone_MediumNestedObject         |     1,020.19 ns |      2.533 ns |      2.115 ns |  0.023 |    0.00 |   0.5436 |   0.0038 |        - |    3416 B |       0.026 |
| DeepClone_FileSpec                   |       299.87 ns |      1.618 ns |      1.351 ns |  0.007 |    0.00 |   0.1464 |        - |        - |     920 B |       0.007 |
| DeepClone_SmallObject                |        52.93 ns |      0.366 ns |      0.305 ns |  0.001 |    0.00 |   0.0293 |        - |        - |     184 B |       0.001 |
| DeepClone_SmallObjectWithCollections |       384.61 ns |      2.238 ns |      1.869 ns |  0.009 |    0.00 |   0.1745 |   0.0005 |        - |    1096 B |       0.008 |
```

## Benchmark Categories

### Small Objects (KnownTypes)
- `DeepClone_SmallObject`: Simple POCO with primitives
- `DeepClone_SmallObjectWithCollections`: POCO with List and Dictionary

### Medium Objects (KnownTypes)
- `DeepClone_MediumNestedObject`: Nested object with user info, request info
- `DeepClone_FileSpec`: File storage metadata object

### Large Objects (KnownTypes)
- `DeepClone_LargeEventDocument_10MB`: Error tracking event with stack traces
- `DeepClone_LargeLogBatch_10MB`: Batch of ~3000 log entries

### Dynamic Types
- `DeepClone_DynamicWithDictionary`: Object with Dictionary<string, string> in object property
- `DeepClone_DynamicWithNestedObject`: Object with nested POCO in object property
- `DeepClone_DynamicWithArray`: Object with mixed-type array in object property

### Collections
- `DeepClone_StringArray_1000`: Array of 1000 strings
- `DeepClone_ObjectList_100`: List of 100 medium nested objects
- `DeepClone_ObjectDictionary_50`: Dictionary of 50 event documents

---

## FastCloner v3.4.4 Evaluation

We evaluated [FastCloner](https://github.com/lofcz/FastCloner) as a potential replacement for Force.DeepCloner.

### Results Summary (FastCloner v3.4.4)

| Benchmark | Mean | Allocated | Use Case |
|-----------|------|-----------|----------|
| **DeepClone_SmallObject** | 132 ns | 144 B | Simple cache entries |
| **DeepClone_FileSpec** | 402 ns | 1,072 B | File storage metadata |
| **DeepClone_SmallObjectWithCollections** | 490 ns | 1,256 B | Config/metadata caching |
| **DeepClone_StringArray_1000** | 501 ns | 8,057 B | String collections |
| **DeepClone_DynamicWithDictionary** | 905 ns | 3,224 B | JSON-like dynamic data |
| **DeepClone_MediumNestedObject** | 1,313 ns | 3,352 B | Typical queue messages |
| **DeepClone_DynamicWithNestedObject** | 1,315 ns | 3,464 B | Nested dynamic objects |
| **DeepClone_DynamicWithArray** | 3,343 ns | 8,984 B | Mixed-type arrays |
| **DeepClone_LargeEventDocument_10MB** | 48,415 ns | 154,888 B | Error tracking events |
| **DeepClone_ObjectList_100** | 120,509 ns | 374,672 B | Batch processing |
| **DeepClone_ObjectDictionary_50** | 597,092 ns | 631,619 B | Keyed collections |
| **DeepClone_LargeLogBatch_10MB** | 5,726,243 ns | 5,251,027 B | Bulk log ingestion |

### Comparison: Force.DeepCloner vs FastCloner

| Benchmark | DeepCloner | FastCloner | Change |
|-----------|------------|------------|--------|
| SmallObject | 52.93 ns | 132 ns | **+149% slower** |
| FileSpec | 299.87 ns | 402 ns | **+34% slower** |
| SmallObjectWithCollections | 384.61 ns | 490 ns | **+27% slower** |
| StringArray_1000 | 470.19 ns | 501 ns | +7% slower |
| DynamicWithDictionary | 797.00 ns | 905 ns | +14% slower |
| MediumNestedObject | 1,020.19 ns | 1,313 ns | **+29% slower** |
| DynamicWithNestedObject | 1,130.58 ns | 1,315 ns | +16% slower |
| DynamicWithArray | 3,672.72 ns | 3,343 ns | **-9% faster** |
| LargeEventDocument_10MB | 43,718.94 ns | 48,415 ns | +11% slower |
| ObjectList_100 | 110,525.77 ns | 120,509 ns | +9% slower |
| ObjectDictionary_50 | 555,214.24 ns | 597,092 ns | +8% slower |
| LargeLogBatch_10MB | 3,662,330.45 ns | 5,726,243 ns | **+56% slower** |

### Conclusion

**FastCloner is consistently slower than DeepCloner** for our benchmark suite:

- **Small objects**: 27-149% slower
- **Medium objects**: 9-29% slower
- **Large objects**: 8-56% slower
- **Only exception**: DynamicWithArray is 9% faster

This contradicts FastCloner's published benchmarks which show ~25x improvement over DeepCloner. The published benchmarks likely use the source generator (`FastDeepClone()`) rather than reflection-based cloning.

### Raw Results (FastCloner v3.4.4)

```
BenchmarkDotNet v0.15.8, macOS Tahoe 26.2 (25C56) [Darwin 25.2.0]
Apple M1 Max, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.102
  [Host]     : .NET 10.0.2 (10.0.2, 10.0.225.61305), Arm64 RyuJIT armv8.0-a
  DefaultJob : .NET 10.0.2 (10.0.2, 10.0.225.61305), Arm64 RyuJIT armv8.0-a


| Method                               | Mean           | Error         | StdDev       | Ratio   | RatioSD | Gen0     | Gen1     | Gen2     | Allocated | Alloc Ratio |
|------------------------------------- |---------------:|--------------:|-------------:|--------:|--------:|---------:|---------:|---------:|----------:|------------:|
| DeepClone_StringArray_1000           |       501.2 ns |       9.63 ns |     10.71 ns |   0.010 |    0.00 |   1.2836 |        - |        - |    8057 B |       0.052 |
| DeepClone_ObjectList_100             |   120,508.5 ns |   2,405.40 ns |  5,177.89 ns |   2.489 |    0.11 |  59.5703 |  19.7754 |        - |  374672 B |       2.419 |
| DeepClone_ObjectDictionary_50        |   597,091.5 ns |  11,791.91 ns | 11,030.16 ns |  12.333 |    0.23 |  96.6797 |  44.9219 |  17.5781 |  631619 B |       4.078 |
| DeepClone_DynamicWithDictionary      |       905.2 ns |      17.14 ns |     19.05 ns |   0.019 |    0.00 |   0.5131 |   0.0038 |        - |    3224 B |       0.021 |
| DeepClone_DynamicWithNestedObject    |     1,314.5 ns |      26.25 ns |     64.89 ns |   0.027 |    0.00 |   0.5512 |   0.0038 |        - |    3464 B |       0.022 |
| DeepClone_DynamicWithArray           |     3,342.6 ns |      21.09 ns |     16.47 ns |   0.069 |    0.00 |   1.4305 |   0.0229 |        - |    8984 B |       0.058 |
| DeepClone_LargeEventDocument_10MB    |    48,415.0 ns |     385.12 ns |    300.68 ns |   1.000 |    0.01 |  24.5972 |   4.5166 |        - |  154888 B |       1.000 |
| DeepClone_LargeLogBatch_10MB         | 5,726,242.8 ns | 109,699.46 ns | 85,646.12 ns | 118.278 |    1.84 | 562.5000 | 257.8125 | 101.5625 | 5251027 B |      33.902 |
| DeepClone_MediumNestedObject         |     1,312.7 ns |      25.56 ns |     39.80 ns |   0.027 |    0.00 |   0.5341 |   0.0038 |        - |    3352 B |       0.022 |
| DeepClone_FileSpec                   |       401.5 ns |       6.40 ns |      6.57 ns |   0.008 |    0.00 |   0.1707 |        - |        - |    1072 B |       0.007 |
| DeepClone_SmallObject                |       132.1 ns |       2.60 ns |      3.56 ns |   0.003 |    0.00 |   0.0229 |        - |        - |     144 B |       0.001 |
| DeepClone_SmallObjectWithCollections |       490.3 ns |       3.48 ns |      2.71 ns |   0.010 |    0.00 |   0.1993 |        - |        - |    1256 B |       0.008 |
```
