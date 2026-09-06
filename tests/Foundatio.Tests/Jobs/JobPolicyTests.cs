using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Foundatio.Tests.Jobs;

public class JobPolicyTests
{
    [Fact]
    public async Task HistoryPressure_PreservesAdmissionAndIdempotency()
    {
        var token = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var store = new InMemoryJobRuntimeStore(new JobRuntimeStoreOptions { MaxActiveJobs = 1, MaxHistoryJobs = 1, MaxDeduplicationRecords = 10 }, time);
        for (int i = 0; i < 3; i++)
        {
            await store.CreateIfAbsentAsync(new JobState { JobId = $"job-{i}", Name = "work", JobType = "work" }, token);
            var claim = await store.ClaimNextAsync(new JobClaimRequest { JobTypes = ["work"], NodeId = "node" }, token);
            Assert.NotNull(claim);
            Assert.True(await store.CompleteJobAsync(claim.JobId, claim.ClaimToken!, new JobCompletion { Kind = JobCompletionKind.Succeeded, Message = "Done" }, token));
        }
        await store.CleanupAsync(cancellationToken: token);
        Assert.Single((await store.QueryAsync(new JobQuery(), token)));
        await store.CreateIfAbsentAsync(new JobState { JobId = "job-0", Name = "work", JobType = "work" }, token);
        Assert.Null(await store.ClaimNextAsync(new JobClaimRequest { JobTypes = ["work"], NodeId = "node" }, token));
        var stats = await store.GetStatsAsync(token);
        Assert.Equal(0, stats.ActiveJobs);
        Assert.Equal(1, stats.HistoryJobs);
        Assert.Equal(3, stats.DeduplicationRecords);
    }

    [Fact]
    public async Task EnqueueAsync_Delay_DefersClaimAndWaitCancellationDoesNotCancelJob()
    {
        var token = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var store = new InMemoryJobRuntimeStore(time);
        var client = new JobClient(store, time, new JobTypeRegistry([new("work", typeof(Work))]));
        var handle = await client.EnqueueAsync<Work>(new JobRequestOptions { Delay = TimeSpan.FromMinutes(5) }, token);
        var request = new JobClaimRequest { JobTypes = ["work"], NodeId = "node" };
        Assert.Null(await store.ClaimNextAsync(request, token));
        using var cancelWait = CancellationTokenSource.CreateLinkedTokenSource(token);
        await cancelWait.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handle.WaitForCompletionAsync(cancellationToken: cancelWait.Token));
        Assert.False((await handle.GetStateAsync(token))!.CancellationRequested);
        time.Advance(TimeSpan.FromMinutes(5));
        var claim = Assert.IsType<JobState>(await store.ClaimNextAsync(request, token));
        await store.CompleteJobAsync(claim.JobId, claim.ClaimToken!, new JobCompletion { Kind = JobCompletionKind.Succeeded, Message = "Done" }, token);
        var final = await handle.WaitForCompletionAsync(cancellationToken: token);
        Assert.Null(final.Error);
        Assert.Equal("Done", final.ResultMessage);
    }

    [Fact]
    public async Task EnqueueAsync_ConflictingDelayAndRunAt_FailsBeforePersistence()
    {
        var store = new InMemoryJobRuntimeStore();
        var client = new JobClient(store);
        await Assert.ThrowsAsync<ArgumentException>(() => client.EnqueueAsync<Work>(new JobRequestOptions { Delay = TimeSpan.FromSeconds(1), RunAt = DateTimeOffset.UtcNow }, TestContext.Current.CancellationToken));
        Assert.Empty((await store.QueryAsync(new JobQuery(), TestContext.Current.CancellationToken)));
    }

    public sealed class Work : IJob
    {
        public Task<JobResult> RunAsync(JobExecutionContext context) => Task.FromResult(JobResult.Success);
    }
}
