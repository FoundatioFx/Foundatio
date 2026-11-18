using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Foundatio.Caching;

namespace Foundatio.Benchmarks;

[MemoryDiagnoser]
public class InMemoryOrderedListBenchmarks
{
    [Params(1_000, 10_000)]
    public int Count { get; set; }

    private readonly int[] _item = [-1];
    private InMemoryCacheClient _cache = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _cache = new InMemoryCacheClient(o => o.CloneValues(false));
        var members = new OrderedDictionary<int, DateTime?>(Count);
        for (int i = 0; i < Count; i++)
            members.Add(i, null);

        await _cache.SetAsync("set", members);
        await AddRemove();
    }

    [Benchmark]
    public async Task<long> AddRemove()
    {
        await _cache.ListAddAsync("set", _item);
        return await _cache.ListRemoveAsync("set", _item);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        using var cache = _cache;
        var members = (await cache.GetAsync<OrderedDictionary<int, DateTime?>>("set")).Value;
        if (!members.Keys.SequenceEqual(Enumerable.Range(0, Count)))
            throw new InvalidOperationException("Ordered list membership changed during the benchmark.");
    }
}
