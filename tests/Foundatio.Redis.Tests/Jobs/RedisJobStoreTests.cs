using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Jobs.Store;
using Foundatio.Tests.Jobs;
using Foundatio.Tests.Utility;
using StackExchange.Redis;
using StackExchange.Redis.Profiling;
using Xunit;

namespace Foundatio.Redis.Tests.Jobs;

public class RedisJobStoreTests(ITestOutputHelper output) : JobStoreTestsBase(output)
{
    protected override IJobStore CreateStore()
    {
        var muxer = SharedConnection.GetMuxer(Log);
        Assert.SkipWhen(muxer is null, "RedisConnectionString is not configured.");
        return new RedisJobStore(muxer, new RedisJobStoreOptions { Prefix = "foundatio-tests:jobs" });
    }

    /// <summary>
    /// Servers such as Dragonfly reject a script that touches a key it was not given, so every key must come from
    /// KEYS (directly or through a local), never be built from the prefix, a function or ARGV.
    /// </summary>
    [Fact]
    public void Scripts_TouchOnlyTheKeysTheyAreGiven()
    {
        var assembly = typeof(RedisJobStore).Assembly;
        var scripts = assembly.GetManifestResourceNames().Where(n => n.StartsWith("Foundatio.Redis.Scripts.JobStore.", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(scripts);

        foreach (string script in scripts)
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(script)!);
            string text = reader.ReadToEnd();
            foreach (Match call in Regex.Matches(text, @"redis\.call\('(?<command>\w+)'(?:,\s*(?<key>[^,)]+))?"))
            {
                if (call.Groups["command"].Value == "TIME")
                    continue;

                string key = call.Groups["key"].Value.Trim();
                Assert.False(key.Contains("..") || key.Contains('(') || key.Contains('\'') || key.Contains("ARGV"), $"{script} builds the key {key} for {call.Groups["command"].Value}.");
            }
        }
    }

    [Fact]
    public async Task GetAndRetry_JobStoredWithoutScheduledFor_FallBackToItsFirstAvailableAt()
    {
        var muxer = SharedConnection.GetMuxer(Log)!;
        var scheduledFor = DateTimeOffset.UtcNow.AddMinutes(-10);
        var job = NewJob() with { AvailableAt = scheduledFor, ScheduledFor = scheduledFor };
        await Store.CreateAsync(job, TestCancellationToken);
        Assert.True(await muxer.GetDatabase().HashDeleteAsync($"{{foundatio-tests:jobs}}:job:{job.Id}", "scheduled"));

        AssertNear(scheduledFor, (await Store.GetAsync(job.Id, TestCancellationToken))!.ScheduledFor);
        var claimed = (await Store.ClaimAsync(Claim(job.Name), TestCancellationToken))!;
        Assert.True(await Store.CompleteAsync(job.Id, claimed.ClaimToken!, JobCompletion.Retry(TimeSpan.FromMinutes(1), "transient"), TestCancellationToken));

        var retried = (await Store.GetAsync(job.Id, TestCancellationToken))!;
        AssertNear(scheduledFor, retried.ScheduledFor);
        Assert.True(retried.AvailableAt > DateTimeOffset.UtcNow.AddSeconds(30));

        static void AssertNear(DateTimeOffset expected, DateTimeOffset actual) => Assert.True((expected - actual).Duration() < TimeSpan.FromSeconds(1), $"Expected {expected:O}, got {actual:O}.");
    }

    [Fact]
    public async Task ClaimAsync_WithAnExpiredBacklogOfSeveralBatches_RemovesItInOneSweep()
    {
        var muxer = SharedConnection.GetMuxer(Log)!;
        string prefix = NewPrefix();
        await CreateExpiredBacklogAsync(new RedisJobStore(muxer, new RedisJobStoreOptions { Prefix = prefix }), NewName(), 600);

        Assert.Null(await new RedisJobStore(muxer, new RedisJobStoreOptions { Prefix = prefix }).ClaimAsync(Claim(NewName()), TestCancellationToken));

        // The claim does not wait for the sweep it started, and nothing else sweeps, so that one sweep removes them all.
        var database = muxer.GetDatabase();
        await WaitUntilAsync(async () => await database.SortedSetLengthAsync($"{{{prefix}}}:expiring") == 0);
        Assert.Equal(0, await database.SortedSetLengthAsync($"{{{prefix}}}:index"));
    }

    [Fact]
    public async Task ClaimAsync_WhenItStartsASweep_ReturnsWithoutWaitingForIt()
    {
        var gate = new SweepGate();
        var store = new RedisJobStore(gate.Wrap(SharedConnection.GetMuxer(Log)!), new RedisJobStoreOptions { Prefix = NewPrefix() });
        var job = NewJob();
        Assert.True(await store.CreateAsync(job, TestCancellationToken));

        try
        {
            var claimed = await store.ClaimAsync(Claim(job.Name), TestCancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);
            Assert.Equal(job.Id, claimed?.Id);
            Assert.Equal(1, gate.Held);

            for (int i = 0; i < 5; i++)
                Assert.Null(await store.ClaimAsync(Claim(job.Name), TestCancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken));
            Assert.Equal(1, gate.Held);
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task QueryAsync_WhenItStartsASweep_ReturnsWithoutWaitingForIt()
    {
        var gate = new SweepGate();
        var store = new RedisJobStore(gate.Wrap(SharedConnection.GetMuxer(Log)!), new RedisJobStoreOptions { Prefix = NewPrefix() });
        var job = NewJob();
        Assert.True(await store.CreateAsync(job, TestCancellationToken));

        try
        {
            var page = await store.QueryAsync(new JobQuery { Name = job.Name }, TestCancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestCancellationToken);
            Assert.Equal(job.Id, Assert.Single(page.Jobs).Id);
            Assert.Equal(1, gate.Held);
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task ClaimAsync_WhenTheSweepFails_ClaimsAndLeavesTheFailureToCounts()
    {
        var gate = new SweepGate { Failure = new InvalidOperationException("The sweep failed.") };
        gate.Release();
        var store = new RedisJobStore(gate.Wrap(SharedConnection.GetMuxer(Log)!), new RedisJobStoreOptions { Prefix = NewPrefix() });
        var job = NewJob();
        Assert.True(await store.CreateAsync(job, TestCancellationToken));

        var claimed = await store.ClaimAsync(Claim(job.Name), TestCancellationToken);

        Assert.Equal(job.Id, claimed?.Id);
        Assert.Equal(1, gate.Held);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CountAsync(JobState.Succeeded, job.Name, TestCancellationToken));
        Assert.Equal(1, await store.CountAsync(JobState.Running, job.Name, TestCancellationToken));
    }

    [Fact]
    public async Task CountAsync_WithAnExpiredBacklog_LeavesOutTheExpiredJobs()
    {
        var store = new RedisJobStore(SharedConnection.GetMuxer(Log)!, new RedisJobStoreOptions { Prefix = NewPrefix() });
        string name = NewName();
        await CreateExpiredBacklogAsync(store, name, 1200);

        Assert.Equal(0, await store.CountAsync(JobState.Cancelled, name, TestCancellationToken));
        Assert.Equal(0, await store.CountAsync(name: name, cancellationToken: TestCancellationToken));
        Assert.Equal(0, await store.CountAsync(cancellationToken: TestCancellationToken));
    }

    [Fact]
    public async Task CountAsync_CalledConcurrentlyWithAnExpiredBacklog_SharesOneSweep()
    {
        string? connectionString = Configuration.GetConnectionString("RedisConnectionString");
        Assert.SkipWhen(String.IsNullOrEmpty(connectionString), "RedisConnectionString is not configured.");
        await using var connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var store = new RedisJobStore(connection, new RedisJobStoreOptions { Prefix = NewPrefix() });
        string name = NewName();
        await CreateExpiredBacklogAsync(store, name, 600);

        var session = new ProfilingSession();
        connection.RegisterProfiler(() => session);
        var counts = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => store.CountAsync(JobState.Cancelled, name, TestCancellationToken)));
        int commands = session.FinishProfiling().Count();

        Assert.All(counts, count => Assert.Equal(0, count));
        Assert.True(commands < 100, $"20 counts sent {commands} commands.");
    }

    [Fact]
    public async Task ClaimRenewAndComplete_RacingAcrossConnections_CompleteEachJobExactlyOnce()
    {
        string? connectionString = Configuration.GetConnectionString("RedisConnectionString");
        Assert.SkipWhen(String.IsNullOrEmpty(connectionString), "RedisConnectionString is not configured.");
        var connections = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ConnectionMultiplexer.ConnectAsync(connectionString)));
        try
        {
            string prefix = $"foundatio-tests:race-{Guid.NewGuid():N}"[..32];
            var stores = connections.Select(c => new RedisJobStore(c, new RedisJobStoreOptions { Prefix = prefix })).ToArray();
            const string name = "race";
            const int jobCount = 100;
            var now = DateTimeOffset.UtcNow;
            for (int i = 0; i < jobCount; i++)
                Assert.True(await stores[0].CreateAsync(new JobInfo { Id = $"job-{i}", Name = name, CreatedAt = now, UpdatedAt = now, AvailableAt = now.AddMinutes(-1) }, TestCancellationToken));

            var completions = new ConcurrentDictionary<string, int>();
            var claims = new ConcurrentDictionary<string, int>();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));

            // Some runs outlive their 300 ms lease, so other workers reclaim them and the first worker's renewals,
            // progress and completion must all be fenced out.
            await Parallel.ForEachAsync(Enumerable.Range(0, 16), timeout.Token, async (worker, token) =>
            {
                var store = stores[worker % stores.Length];
                var random = new Random(worker);
                while (completions.Count < jobCount)
                {
                    var job = await store.ClaimAsync(new JobClaimRequest { Names = [name], WorkerId = $"worker-{worker}", LeaseDuration = TimeSpan.FromMilliseconds(300) }, token);
                    if (job is null)
                    {
                        await Task.Delay(20, token);
                        continue;
                    }

                    claims.AddOrUpdate(job.Id, 1, (_, count) => count + 1);
                    await Task.Delay(random.Next(0, 500), token);
                    if (random.Next(3) == 0)
                        await store.RenewAsync(job.Id, job.ClaimToken!, TimeSpan.FromMilliseconds(300), token);
                    await store.ReportProgressAsync(job.Id, job.ClaimToken!, 50, null, token);

                    if (await store.CompleteAsync(job.Id, job.ClaimToken!, JobCompletion.Succeeded(TimeSpan.FromMinutes(5)), token))
                        completions.AddOrUpdate(job.Id, 1, (_, count) => count + 1);
                }
            });

            Assert.Equal(jobCount, completions.Count);
            Assert.All(completions.Values, count => Assert.Equal(1, count));
            Assert.Contains(claims.Values, count => count > 1);
            Assert.Equal(jobCount, await stores[1].CountAsync(JobState.Succeeded, name, TestCancellationToken));
            Assert.Equal(0, await stores[1].CountAsync(JobState.Running, name, TestCancellationToken));
            Assert.Equal(0, await stores[1].CountAsync(JobState.Queued, name, TestCancellationToken));
            foreach (var job in (await stores[2].QueryAsync(new JobQuery { Name = name, Limit = 1000 }, TestCancellationToken)).Jobs)
            {
                Assert.Null(job.ClaimToken);
                Assert.Null(job.LeaseExpiresAt);
                Assert.Equal(claims[job.Id], job.Attempt);
            }
        }
        finally
        {
            foreach (var connection in connections)
                await connection.DisposeAsync();
        }
    }

    private static string NewPrefix() => $"foundatio-tests:sweep-{Guid.NewGuid():N}"[..36];

    /// <summary>Creates <paramref name="count"/> jobs and cancels them with a 1 ms retention, then waits until they have all expired.</summary>
    private async Task CreateExpiredBacklogAsync(RedisJobStore store, string name, int count)
    {
        var jobs = Enumerable.Range(0, count).Select(_ => NewJob(name)).ToList();
        foreach (var chunk in jobs.Chunk(100))
        {
            Assert.All(await Task.WhenAll(chunk.Select(job => store.CreateAsync(job, TestCancellationToken))), Assert.True);
            Assert.All(await Task.WhenAll(chunk.Select(job => store.RequestCancellationAsync(job.Id, TimeSpan.FromMilliseconds(1), TestCancellationToken))), Assert.True);
        }

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (await store.GetAsync(jobs[^1].Id, TestCancellationToken) is not null)
        {
            Assert.True(DateTime.UtcNow < deadline, "The backlog did not expire in time.");
            await Task.Delay(10, TestCancellationToken);
        }
    }
}
