using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Foundatio.Caching;
using Foundatio.Lock;
using Foundatio.Messaging;

namespace Foundatio.Benchmarks;

[MemoryDiagnoser]
public class MultiLockBenchmarks
{
    private readonly InMemoryCacheClient _cache = new();
    private readonly InMemoryMessageBus _bus = new();
    private CacheLockProvider _provider = null!;
    private string[] _resources = null!;

    [Params(2, 16)]
    public int ResourceCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _provider = new CacheLockProvider(_cache, _bus);
        _resources = Enumerable.Range(0, ResourceCount).Select(i => $"lock-{i}").ToArray();
    }

    [Benchmark]
    public async Task AcquireRenewAndReleaseAsync()
    {
        await using var leases = await _provider.AcquireAsync(_resources);
        await leases.RenewAsync();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _bus.Dispose();
        _cache.Dispose();
    }
}
