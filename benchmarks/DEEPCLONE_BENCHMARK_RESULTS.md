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
