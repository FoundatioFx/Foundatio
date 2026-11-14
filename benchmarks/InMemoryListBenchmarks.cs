using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Foundatio.Caching;

namespace Foundatio.Benchmarks;

[MemoryDiagnoser]
public class InMemoryListBenchmarks
{
    public enum SizingMode { None, Fixed, Custom, Dynamic }

    [Params(false, true)]
    public bool CloneValues { get; set; }

    [Params(10, 1000)]
    public int Count { get; set; }

    [Params(SizingMode.None, SizingMode.Fixed, SizingMode.Custom, SizingMode.Dynamic)]
    public SizingMode Sizing { get; set; }

    private InMemoryCacheClient _cache = null!;
    private int[] _initial = null!;
    private int[] _batch = null!;
    private readonly int[] _item = [-1];
    private readonly int[] _workers = [0, 1, 2, 3];

    [GlobalSetup]
    public async Task Setup()
    {
        _cache = new InMemoryCacheClient(o =>
        {
            o.CloneValues(CloneValues).TimeProvider(new FixedTimeProvider());
            return Sizing switch
            {
                SizingMode.Fixed => o.WithFixedSizing(100000000, 100),
                SizingMode.Custom => o.MaxMemorySize(100000000).SizeCalculator(_ => 100),
                SizingMode.Dynamic => o.WithDynamicSizing(100000000),
                _ => o
            };
        });
        _initial = Enumerable.Range(0, Count).ToArray();
        _batch = Enumerable.Range(0, Count / 2).ToArray();
        await _cache.ListAddAsync("set", _initial);
    }

    [GlobalCleanup]
    public void Cleanup() => _cache.Dispose();

    [Benchmark]
    public async Task<long> AddRemove()
    {
        await _cache.ListAddAsync("set", _item);
        return await _cache.ListRemoveAsync("set", _item);
    }

    [Benchmark]
    public async Task<long> BulkInsert()
    {
        await _cache.RemoveAsync("bulk");
        return await _cache.ListAddAsync("bulk", _initial);
    }

    [Benchmark]
    public async Task<long> BulkRemoveRestore()
    {
        await _cache.ListRemoveAsync("set", _batch);
        return await _cache.ListAddAsync("set", _batch);
    }

    [Benchmark]
    public Task ContendedWrites() => Parallel.ForEachAsync(_workers, async (worker, token) =>
    {
        int[] item = [-worker - 1];
        await Task.Yield();
        await _cache.ListAddAsync("set", item);
        await _cache.ListRemoveAsync("set", item);
    });

    [Benchmark]
    public async Task<long> FirstModification()
    {
        await _cache.RemoveAsync("cold");
        await _cache.ListAddAsync("cold", _initial);
        return await _cache.ListAddAsync("cold", _item);
    }

    [Benchmark]
    public Task<CacheValue<ICollection<int>>> Read() => _cache.GetListAsync<int>("set");

    [Benchmark]
    public Task<long> RemoveAbsent() => _cache.ListRemoveAsync("set", _item);

    [Benchmark]
    public async Task<CacheValue<ICollection<int>>> WriteThenRead()
    {
        await AddRemove();
        return await _cache.GetListAsync<int>("set");
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
