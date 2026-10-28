using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Foundatio.Caching;

namespace Foundatio.Benchmarks;

/// <summary>
/// Scalar writes on a key that holds a live value, which is the common case. Every write goes through
/// <c>UpdateEntry</c>, so these show what its wrapper costs.
/// </summary>
[MemoryDiagnoser]
public class InMemoryWriteBenchmarks
{
    private InMemoryCacheClient _cache = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _cache = new InMemoryCacheClient(o => o.CloneValues(false));
        await _cache.SetAsync("key", 1L);
    }

    [GlobalCleanup]
    public void Cleanup() => _cache.Dispose();

    [Benchmark]
    public Task<bool> Set() => _cache.SetAsync("key", 1L);

    [Benchmark]
    public Task<bool> Add_ExistingKey() => _cache.AddAsync("key", 1L);

    [Benchmark]
    public Task<long> Increment() => _cache.IncrementAsync("key", 1L);

    [Benchmark]
    public Task<long> SetIfHigher_NotHigher() => _cache.SetIfHigherAsync("key", 0L);

    [Benchmark]
    public Task<bool> ReplaceIfEqual() => _cache.ReplaceIfEqualAsync("key", 1L, 1L);
}
