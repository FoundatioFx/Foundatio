#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Foundatio.Jobs;
using Foundatio.Messaging;

namespace Foundatio.Benchmarks;

[MemoryDiagnoser]
public class MessageHeadersBenchmarks
{
    [Params(6, 16)]
    public int Count { get; set; }

    private KeyValuePair<string, string>[] _values = null!;
    private MessageHeaders _headers = null!;

    [GlobalSetup]
    public void Setup()
    {
        _values = Enumerable.Range(0, Count).Select(i => new KeyValuePair<string, string>($"message.header{i}", $"value-{i}")).ToArray();
        _headers = MessageHeaders.Create(_values);
    }

    [Benchmark]
    public MessageHeaders Construct() => MessageHeaders.Create(_values);

    [Benchmark]
    public string Serialize() => MessageHeaders.SerializeToJson(_headers);
}

[MemoryDiagnoser]
public class JobPollingBenchmarks
{
    [Params(0, 10000)]
    public int History { get; set; }

    private InMemoryJobRuntimeStore _store = null!;
    private JobClaimRequest _request = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _store = new InMemoryJobRuntimeStore();
        for (int i = 0; i < History; i++)
            await _store.CreateIfAbsentAsync(new JobState
            {
                JobId = $"old-{i}", Name = "work", JobType = "work", Status = JobStatus.Completed,
                CompletedUtc = DateTimeOffset.UtcNow
            });
        _request = new JobClaimRequest { NodeId = "worker", JobTypes = ["work"] };
    }

    [Benchmark]
    public Task<JobState?> EmptyClaim() => _store.ClaimNextAsync(_request);
}
