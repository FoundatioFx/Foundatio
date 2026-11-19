using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Foundatio.Caching;
using Foundatio.Messaging;

namespace Foundatio.Benchmarks;

[MemoryDiagnoser]
public class HybridListBenchmarks
{
    private readonly InMemoryCacheClient _distributed = new();
    private readonly InMemoryMessageBus _bus = new();
    private readonly int[] _member = [1];
    private HybridCacheClient _cache = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _cache = new HybridCacheClient(_distributed, _bus);
        await _cache.ListAddAsync("warm", [0]);
    }

    [Benchmark]
    public async Task CreateAndRemoveListAsync()
    {
        await _cache.ListAddAsync("new", _member);
        await _cache.RemoveAsync("new");
    }

    [Benchmark]
    public async Task UpdateWarmListAsync()
    {
        await _cache.ListAddAsync("warm", _member);
        await _cache.ListRemoveAsync("warm", _member);
    }

    [Benchmark]
    public Task<bool> SetScalarAsync() => _cache.SetAsync("scalar", 1);

    [GlobalCleanup]
    public void Cleanup()
    {
        _cache.Dispose();
        _bus.Dispose();
        _distributed.Dispose();
    }
}
