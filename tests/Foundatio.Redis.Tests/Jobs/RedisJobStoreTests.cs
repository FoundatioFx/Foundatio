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
using Xunit;

namespace Foundatio.Redis.Tests.Jobs;

public class RedisJobStoreTests(ITestOutputHelper output) : JobStoreTestsBase(output)
{
    protected override IJobStore CreateStore()
    {
        var muxer = SharedConnection.GetMuxer(Log);
        Assert.SkipWhen(muxer is null, "RedisConnectionString is not configured.");
        return new RedisJobStore(muxer, new RedisJobStoreOptions { KeyPrefix = "foundatio-tests:jobs" });
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
    public async Task ClaimRenewAndComplete_RacingAcrossConnections_CompleteEachJobExactlyOnce()
    {
        string? connectionString = Configuration.GetConnectionString("RedisConnectionString");
        Assert.SkipWhen(String.IsNullOrEmpty(connectionString), "RedisConnectionString is not configured.");
        var connections = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ConnectionMultiplexer.ConnectAsync(connectionString)));
        try
        {
            string prefix = $"foundatio-tests:race-{Guid.NewGuid():N}"[..32];
            var stores = connections.Select(c => new RedisJobStore(c, new RedisJobStoreOptions { KeyPrefix = prefix })).ToArray();
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
            Assert.Equal(jobCount, await stores[1].CountAsync(JobStatus.Succeeded, name, TestCancellationToken));
            Assert.Equal(0, await stores[1].CountAsync(JobStatus.Running, name, TestCancellationToken));
            Assert.Equal(0, await stores[1].CountAsync(JobStatus.Queued, name, TestCancellationToken));
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
}
