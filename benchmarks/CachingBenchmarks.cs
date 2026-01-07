using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Foundatio.Caching;

namespace Foundatio.Benchmarks;

/// <summary>
/// Benchmarks comparing InMemoryCacheClient performance across different sizing configurations:
/// - Default: No memory limits (baseline)
/// - FixedSizing: WithFixedSizing() for uniform entry sizes
/// - DynamicSizing: WithDynamicSizing() for object-based size calculation
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
[BenchmarkCategory("Caching")]
public class CachingBenchmarks
{
    private InMemoryCacheClient _defaultCache;
    private InMemoryCacheClient _fixedSizingCache;
    private InMemoryCacheClient _dynamicSizingCache;

    private const string TestKey = "test-key";
    private const string TestValue = "test-value-string-with-some-content";
    private const long MaxMemorySize = 10 * 1024 * 1024; // 10 MB
    private const long FixedEntrySize = 100; // 100 bytes per entry
    private const int MaxItems = 100_000;

    private ComplexTestObject _complexObject;
    private string[] _bulkKeys;
    private string[] _bulkValues;
    private const int BulkCount = 100;

    [GlobalSetup]
    public void Setup()
    {
        // Default cache - no memory limits (baseline)
        _defaultCache = new InMemoryCacheClient(o => o.MaxItems(MaxItems));

        // Fixed sizing cache - constant size per entry (fastest with memory limits)
        _fixedSizingCache = new InMemoryCacheClient(o => o
            .MaxItems(MaxItems)
            .WithFixedSizing(MaxMemorySize, FixedEntrySize));

        // Dynamic sizing cache - calculates actual object sizes
        _dynamicSizingCache = new InMemoryCacheClient(o => o
            .MaxItems(MaxItems)
            .WithDynamicSizing(MaxMemorySize));

        // Prepare test data
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
        _defaultCache?.Dispose();
        _fixedSizingCache?.Dispose();
        _dynamicSizingCache?.Dispose();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Set", "String")]
    public async Task SetAsync_String_Default()
    {
        await _defaultCache.SetAsync(TestKey, TestValue);
    }

    [Benchmark]
    [BenchmarkCategory("Set", "String")]
    public async Task SetAsync_String_FixedSizing()
    {
        await _fixedSizingCache.SetAsync(TestKey, TestValue);
    }

    [Benchmark]
    [BenchmarkCategory("Set", "String")]
    public async Task SetAsync_String_DynamicSizing()
    {
        await _dynamicSizingCache.SetAsync(TestKey, TestValue);
    }

    [Benchmark]
    [BenchmarkCategory("Set", "ComplexObject")]
    public async Task SetAsync_ComplexObject_Default()
    {
        await _defaultCache.SetAsync(TestKey, _complexObject);
    }

    [Benchmark]
    [BenchmarkCategory("Set", "ComplexObject")]
    public async Task SetAsync_ComplexObject_FixedSizing()
    {
        await _fixedSizingCache.SetAsync(TestKey, _complexObject);
    }

    [Benchmark]
    [BenchmarkCategory("Set", "ComplexObject")]
    public async Task SetAsync_ComplexObject_DynamicSizing()
    {
        await _dynamicSizingCache.SetAsync(TestKey, _complexObject);
    }

    [IterationSetup(Target = nameof(GetAsync_String_Default))]
    public void SetupGetDefault() => _defaultCache.SetAsync(TestKey, TestValue).GetAwaiter().GetResult();

    [IterationSetup(Target = nameof(GetAsync_String_FixedSizing))]
    public void SetupGetFixedSizing() => _fixedSizingCache.SetAsync(TestKey, TestValue).GetAwaiter().GetResult();

    [IterationSetup(Target = nameof(GetAsync_String_DynamicSizing))]
    public void SetupGetDynamicSizing() => _dynamicSizingCache.SetAsync(TestKey, TestValue).GetAwaiter().GetResult();

    [Benchmark]
    [BenchmarkCategory("Get", "String")]
    public async Task<CacheValue<string>> GetAsync_String_Default()
    {
        return await _defaultCache.GetAsync<string>(TestKey);
    }

    [Benchmark]
    [BenchmarkCategory("Get", "String")]
    public async Task<CacheValue<string>> GetAsync_String_FixedSizing()
    {
        return await _fixedSizingCache.GetAsync<string>(TestKey);
    }

    [Benchmark]
    [BenchmarkCategory("Get", "String")]
    public async Task<CacheValue<string>> GetAsync_String_DynamicSizing()
    {
        return await _dynamicSizingCache.GetAsync<string>(TestKey);
    }

    [Benchmark]
    [BenchmarkCategory("SetMany", "String")]
    public async Task SetManyAsync_String_Default()
    {
        var items = new Dictionary<string, string>();
        for (int i = 0; i < BulkCount; i++)
        {
            items[_bulkKeys[i]] = _bulkValues[i];
        }
        await _defaultCache.SetAllAsync(items);
    }

    [Benchmark]
    [BenchmarkCategory("SetMany", "String")]
    public async Task SetManyAsync_String_FixedSizing()
    {
        var items = new Dictionary<string, string>();
        for (int i = 0; i < BulkCount; i++)
        {
            items[_bulkKeys[i]] = _bulkValues[i];
        }
        await _fixedSizingCache.SetAllAsync(items);
    }

    [Benchmark]
    [BenchmarkCategory("SetMany", "String")]
    public async Task SetManyAsync_String_DynamicSizing()
    {
        var items = new Dictionary<string, string>();
        for (int i = 0; i < BulkCount; i++)
        {
            items[_bulkKeys[i]] = _bulkValues[i];
        }
        await _dynamicSizingCache.SetAllAsync(items);
    }

    [IterationSetup(Targets = [nameof(GetManyAsync_String_Default), nameof(GetManyAsync_String_FixedSizing), nameof(GetManyAsync_String_DynamicSizing)])]
    public void SetupGetMany()
    {
        var defaultItems = new Dictionary<string, string>();
        var fixedItems = new Dictionary<string, string>();
        var dynamicItems = new Dictionary<string, string>();
        for (int i = 0; i < BulkCount; i++)
        {
            defaultItems[_bulkKeys[i]] = _bulkValues[i];
            fixedItems[_bulkKeys[i]] = _bulkValues[i];
            dynamicItems[_bulkKeys[i]] = _bulkValues[i];
        }
        _defaultCache.SetAllAsync(defaultItems).GetAwaiter().GetResult();
        _fixedSizingCache.SetAllAsync(fixedItems).GetAwaiter().GetResult();
        _dynamicSizingCache.SetAllAsync(dynamicItems).GetAwaiter().GetResult();
    }

    [Benchmark]
    [BenchmarkCategory("GetMany", "String")]
    public async Task<IDictionary<string, CacheValue<string>>> GetManyAsync_String_Default()
    {
        return await _defaultCache.GetAllAsync<string>(_bulkKeys);
    }

    [Benchmark]
    [BenchmarkCategory("GetMany", "String")]
    public async Task<IDictionary<string, CacheValue<string>>> GetManyAsync_String_FixedSizing()
    {
        return await _fixedSizingCache.GetAllAsync<string>(_bulkKeys);
    }

    [Benchmark]
    [BenchmarkCategory("GetMany", "String")]
    public async Task<IDictionary<string, CacheValue<string>>> GetManyAsync_String_DynamicSizing()
    {
        return await _dynamicSizingCache.GetAllAsync<string>(_bulkKeys);
    }
}

/// <summary>
/// A complex object used to benchmark dynamic size calculation overhead.
/// </summary>
public class ComplexTestObject
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<string> Tags { get; set; }
    public Dictionary<string, string> Metadata { get; set; }
}

