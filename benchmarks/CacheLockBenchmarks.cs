using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Messaging;

namespace Foundatio.Benchmarks;

[MemoryDiagnoser]
public class CacheLockBenchmarks
{
    private readonly InMemoryCacheClient _cache = new();
    private readonly InMemoryMessageBus _bus = new();
    private CacheLockProvider _provider = null!;
    private ILock _lease = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _provider = new CacheLockProvider(_cache, _bus);
        _lease = await _provider.AcquireAsync("renew");
    }

    [Benchmark]
    public async Task AcquireAndReleaseAsync()
    {
        await using var lease = await _provider.AcquireAsync("acquire");
    }

    [Benchmark]
    public Task RenewAsync() => _lease.RenewAsync();

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _lease.DisposeAsync();
        _bus.Dispose();
        _cache.Dispose();
    }
}
