using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Foundatio.Caching;

namespace Foundatio.Benchmarks;

/// <summary>
/// Baseline benchmarks for InMemoryCacheClient - same as main branch for comparison.
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
public class CachingBaselineBenchmarks
{
    private InMemoryCacheClient _cache;

    private const string TestKey = "test-key";
    private const string TestValue = "test-value-string-with-some-content";
    private const int MaxItems = 100_000;

    private ComplexTestObject _complexObject;
    private string[] _bulkKeys;
    private string[] _bulkValues;
    private const int BulkCount = 100;

    [GlobalSetup]
    public void Setup()
    {
        _cache = new InMemoryCacheClient(o => o.MaxItems(MaxItems));

        _complexObject = new ComplexTestObject
        {
            Id = 12345,
            Name = "Test Object",
            Description = "A complex object for benchmarking dynamic size calculation",
            CreatedAt = DateTime.UtcNow,
            Tags = ["tag1", "tag2", "tag3", "tag4", "tag5"],
            Metadata = new Dictionary<string, string>
            {
                ["key1"] = "value1",
                ["key2"] = "value2",
                ["key3"] = "value3"
            }
        };

        _bulkKeys = new string[BulkCount];
        _bulkValues = new string[BulkCount];
        for (int i = 0; i < BulkCount; i++)
        {
            _bulkKeys[i] = $"bulk-key-{i}";
            _bulkValues[i] = $"bulk-value-{i}-with-some-additional-content";
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _cache?.Dispose();
    }

    [Benchmark(Baseline = true)]
    public async Task SetAsync_String()
    {
        await _cache.SetAsync(TestKey, TestValue);
    }

    [Benchmark]
    public async Task SetAsync_ComplexObject()
    {
        await _cache.SetAsync(TestKey, _complexObject);
    }

    [Benchmark]
    public async Task SetManyAsync_String()
    {
        var items = new Dictionary<string, string>();
        for (int i = 0; i < BulkCount; i++)
        {
            items[_bulkKeys[i]] = _bulkValues[i];
        }
        await _cache.SetAllAsync(items);
    }
}

