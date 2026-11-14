using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Jobs.Store;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Foundatio.Tests.Jobs;

public class InMemoryJobStoreTests(ITestOutputHelper output) : JobStoreTestsBase(output)
{
    protected override IJobStore CreateStore() => new InMemoryJobStore();

    [Fact]
    public async Task CountQueryAndGet_AfterRetentionPasses_LeaveOutExpiredJobsWithoutAClaim()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryJobStore(time);
        string name = NewName();
        var jobs = Enumerable.Range(0, 3).Select(i => NewJob(name) with { Id = $"job-{i}", CreatedAt = time.GetUtcNow(), AvailableAt = time.GetUtcNow() }).ToList();
        foreach (var job in jobs)
            await store.CreateAsync(job, TestCancellationToken);
        for (int i = 0; i < jobs.Count; i++)
        {
            var claimed = (await store.ClaimAsync(Claim(name), TestCancellationToken))!;
            await store.CompleteAsync(claimed.Id, claimed.ClaimToken!, JobCompletion.Succeeded(TimeSpan.FromMinutes(i + 1)), TestCancellationToken);
        }

        time.Advance(TimeSpan.FromSeconds(90));

        Assert.Equal(2, await store.CountAsync(JobState.Succeeded, name, TestCancellationToken));
        Assert.Equal(2, await store.CountAsync(cancellationToken: TestCancellationToken));
        Assert.Equal(2, (await store.QueryAsync(new JobQuery { Name = name }, TestCancellationToken)).Jobs.Count);
        Assert.Null(await store.GetAsync("job-0", TestCancellationToken));
        Assert.True(await store.CreateAsync(jobs[0], TestCancellationToken));
        Assert.Equal(JobState.Queued, (await store.GetAsync("job-0", TestCancellationToken))?.State);
    }

    [Fact]
    public async Task Operations_InRandomOrder_KeepClaimsCountsAndQueriesConsistentWithTheStoredJobs()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryJobStore(time);
        var random = new Random(601);
        string[] names = ["a", "b", "c"];
        var ids = new List<string>();
        var running = new Dictionary<string, string>();

        for (int step = 0; step < 2000; step++)
        {
            var now = time.GetUtcNow();
            switch (random.Next(8))
            {
                case 0 or 1:
                    string id = $"job-{ids.Count}";
                    ids.Add(id);
                    var createdAt = now.AddTicks(-random.Next(3) * TimeSpan.TicksPerSecond);
                    await store.CreateAsync(new JobInfo { Id = id, Name = names[random.Next(names.Length)], CreatedAt = createdAt, UpdatedAt = now, AvailableAt = now.AddSeconds(random.Next(-5, 5)) }, TestCancellationToken);
                    break;
                case 2 or 3:
                    var requested = names.Where(_ => random.Next(2) == 0).ToArray();
                    var dueAt = (await GetAllAsync()).Where(j => requested.Contains(j.Name) && j.State is JobState.Queued or JobState.Running)
                        .ToDictionary(j => j.Id, j => j.State == JobState.Queued ? j.AvailableAt : j.LeaseExpiresAt.GetValueOrDefault());
                    var claimed = await store.ClaimAsync(new JobClaimRequest { Names = requested, WorkerId = "worker", LeaseDuration = TimeSpan.FromSeconds(random.Next(1, 10)) }, TestCancellationToken);
                    if (dueAt.Values.All(d => d > now))
                    {
                        Assert.Null(claimed);
                        break;
                    }

                    Assert.NotNull(claimed);
                    Assert.Equal(dueAt.Values.Where(d => d <= now).Min(), dueAt[claimed.Id]);
                    running[claimed.Id] = claimed.ClaimToken!;
                    break;
                case 4 when running.Count > 0:
                    var (runningId, token) = running.ElementAt(random.Next(running.Count));
                    running.Remove(runningId);
                    var completion = random.Next(5) switch
                    {
                        0 => JobCompletion.Succeeded(TimeSpan.FromSeconds(random.Next(0, 20))),
                        1 => JobCompletion.Failed(TimeSpan.FromSeconds(random.Next(0, 20)), "boom"),
                        2 => JobCompletion.Cancelled(TimeSpan.FromSeconds(random.Next(0, 20))),
                        3 => JobCompletion.Retry(TimeSpan.FromSeconds(random.Next(0, 5)), "transient"),
                        _ => JobCompletion.Release()
                    };
                    await store.CompleteAsync(runningId, token, completion, TestCancellationToken);
                    break;
                case 5 when running.Count > 0:
                    var (renewedId, renewedToken) = running.ElementAt(random.Next(running.Count));
                    await store.RenewAsync(renewedId, renewedToken, TimeSpan.FromSeconds(random.Next(1, 10)), TestCancellationToken);
                    break;
                case 6 when ids.Count > 0:
                    string target = ids[random.Next(ids.Count)];
                    if (random.Next(2) == 0)
                        await store.RequestCancellationAsync(target, TimeSpan.FromSeconds(random.Next(0, 20)), TestCancellationToken);
                    else
                        await store.DeleteAsync(target, TestCancellationToken);
                    break;
                default:
                    time.Advance(TimeSpan.FromSeconds(random.Next(0, 3)));
                    break;
            }

            if (step % 50 == 0)
                await AssertCountsAndQueriesAsync();
        }

        await AssertCountsAndQueriesAsync();

        async Task<List<JobInfo>> GetAllAsync()
        {
            var jobs = new List<JobInfo>();
            foreach (string id in ids)
            {
                if (await store.GetAsync(id, TestCancellationToken) is { } job)
                    jobs.Add(job);
            }

            return jobs;
        }

        async Task AssertCountsAndQueriesAsync()
        {
            var jobs = await GetAllAsync();
            foreach (JobState? state in new JobState?[] { null, JobState.Queued, JobState.Running, JobState.Succeeded, JobState.Failed, JobState.Cancelled })
            {
                foreach (string? name in new[] { null, "a", "b", "c" })
                {
                    var expected = jobs.Where(j => (state is null || j.State == state) && (name is null || j.Name == name))
                        .OrderByDescending(j => j.CreatedAt.UtcTicks).ThenByDescending(j => j.Id, StringComparer.Ordinal).Select(j => j.Id).ToList();
                    Assert.Equal(expected.Count, await store.CountAsync(state, name, TestCancellationToken));

                    var pages = new List<string>();
                    string? continuation = null;
                    do
                    {
                        var page = await store.QueryAsync(new JobQuery { State = state, Name = name, Limit = 7, ContinuationToken = continuation }, TestCancellationToken);
                        pages.AddRange(page.Jobs.Select(j => j.Id));
                        continuation = page.ContinuationToken;
                    } while (continuation is not null);

                    Assert.Equal(expected, pages);
                }
            }
        }
    }
}
