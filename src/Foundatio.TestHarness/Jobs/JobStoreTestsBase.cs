using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Jobs.Store;
using Foundatio.Xunit;
using Xunit;

namespace Foundatio.Tests.Jobs;

/// <summary>
/// The conformance suite every <see cref="IJobStore"/> must pass. Derive from it and implement <see cref="CreateStore"/>.
/// Tests use unique job names so they can run in parallel against a shared database.
/// </summary>
/// <example>
/// <code>
/// public class RedisJobStoreTests(ITestOutputHelper output) : JobStoreTestsBase(output)
/// {
///     protected override IJobStore CreateStore() => new RedisJobStore(...);
/// }
/// </code>
/// </example>
public abstract class JobStoreTestsBase : TestWithLoggingBase
{
    protected JobStoreTestsBase(ITestOutputHelper output) : base(output)
    {
    }

    /// <summary>Creates a store connected to the database under test.</summary>
    protected abstract IJobStore CreateStore();

    /// <summary>The lease used by tests that wait for leases to expire.</summary>
    protected virtual TimeSpan ShortLease => TimeSpan.FromSeconds(1);

    protected IJobStore Store => field ??= CreateStore();

    [Fact]
    public async Task Create_ThenGet_ReturnsTheJob()
    {
        var job = NewJob(payload: [1, 2, 3, 0, 255]) with { CorrelationId = "correlation", ScheduleName = "nightly" };

        Assert.True(await Store.CreateAsync(job, TestCancellationToken));

        var stored = await Store.GetAsync(job.Id, TestCancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(job.Name, stored.Name);
        Assert.Equal(JobStatus.Queued, stored.Status);
        Assert.Equal(0, stored.Attempt);
        Assert.Equal([1, 2, 3, 0, 255], stored.Payload.ToArray());
        Assert.Equal("correlation", stored.CorrelationId);
        Assert.Equal("nightly", stored.ScheduleName);
        AssertClose(job.AvailableAt, stored.AvailableAt);
        Assert.Null(await Store.GetAsync(Guid.NewGuid().ToString("N"), TestCancellationToken));
    }

    [Fact]
    public async Task Create_WithAnExistingId_ReturnsFalseAndKeepsTheOriginal()
    {
        var job = NewJob(payload: [1]);
        Assert.True(await Store.CreateAsync(job, TestCancellationToken));

        Assert.False(await Store.CreateAsync(job with { Payload = new byte[] { 2 } }, TestCancellationToken));

        Assert.Equal([1], (await Store.GetAsync(job.Id, TestCancellationToken))!.Payload.ToArray());
    }

    [Fact]
    public async Task Claim_ReturnsADueJobAndMarksItRunning()
    {
        var job = NewJob();
        await Store.CreateAsync(job, TestCancellationToken);

        var claimed = await Store.ClaimAsync(Claim(job.Name, lease: TimeSpan.FromMinutes(1)), TestCancellationToken);

        Assert.NotNull(claimed);
        Assert.Equal(job.Id, claimed.Id);
        Assert.Equal(JobStatus.Running, claimed.Status);
        Assert.Equal(1, claimed.Attempt);
        Assert.False(String.IsNullOrEmpty(claimed.ClaimToken));
        Assert.Equal("worker-1", claimed.WorkerId);
        Assert.NotNull(claimed.StartedAt);
        Assert.True(claimed.LeaseExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30));
        Assert.Equal(JobStatus.Running, (await Store.GetAsync(job.Id, TestCancellationToken))!.Status);
        Assert.Null(await Store.ClaimAsync(Claim(job.Name), TestCancellationToken));
    }

    [Fact]
    public async Task Claim_IgnoresJobsThatAreNotDueOrNotRequested()
    {
        var later = NewJob() with { AvailableAt = DateTimeOffset.UtcNow.AddHours(1) };
        var other = NewJob();
        await Store.CreateAsync(later, TestCancellationToken);
        await Store.CreateAsync(other, TestCancellationToken);

        Assert.Null(await Store.ClaimAsync(Claim(later.Name), TestCancellationToken));
        Assert.Null(await Store.ClaimAsync(Claim(NewName()), TestCancellationToken));
        Assert.Equal(other.Id, (await Store.ClaimAsync(Claim(later.Name, other.Name), TestCancellationToken))?.Id);
    }

    [Fact]
    public async Task Claim_ReturnsTheJobDueLongestFirst()
    {
        string name = NewName();
        var now = DateTimeOffset.UtcNow;
        var newer = NewJob(name) with { AvailableAt = now.AddSeconds(-1) };
        var older = NewJob(name) with { AvailableAt = now.AddMinutes(-5) };
        await Store.CreateAsync(newer, TestCancellationToken);
        await Store.CreateAsync(older, TestCancellationToken);

        Assert.Equal(older.Id, (await Store.ClaimAsync(Claim(name), TestCancellationToken))?.Id);
        Assert.Equal(newer.Id, (await Store.ClaimAsync(Claim(name), TestCancellationToken))?.Id);
    }

    [Fact]
    public async Task Claim_ReturnsTheJobDueLongestAcrossAllRequestedNames()
    {
        var now = DateTimeOffset.UtcNow;
        var recent = NewJob() with { AvailableAt = now.AddSeconds(-1) };
        var overdue = NewJob() with { AvailableAt = now.AddMinutes(-5) };
        await Store.CreateAsync(recent, TestCancellationToken);
        await Store.CreateAsync(overdue, TestCancellationToken);

        Assert.Equal(overdue.Id, (await Store.ClaimAsync(Claim(recent.Name, overdue.Name), TestCancellationToken))?.Id);
    }

    [Fact]
    public async Task Claim_FromConcurrentWorkers_ClaimsEachJobOnce()
    {
        string name = NewName();
        for (int i = 0; i < 20; i++)
            await Store.CreateAsync(NewJob(name), TestCancellationToken);

        var claimed = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(Enumerable.Range(0, 8), TestCancellationToken, async (worker, token) =>
        {
            while (await Store.ClaimAsync(Claim(name) with { WorkerId = $"worker-{worker}" }, token) is { } job)
                claimed.Add(job.Id);
        });

        Assert.Equal(20, claimed.Count);
        Assert.Equal(20, claimed.Distinct().Count());
    }

    [Fact]
    public async Task Claim_AfterTheLeaseExpires_ReclaimsWithANewTokenAndAttempt()
    {
        var job = NewJob();
        await Store.CreateAsync(job, TestCancellationToken);
        var first = (await Store.ClaimAsync(Claim(job.Name, lease: ShortLease), TestCancellationToken))!;
        Assert.Null(await Store.ClaimAsync(Claim(job.Name), TestCancellationToken));

        await Task.Delay(ShortLease + TimeSpan.FromMilliseconds(250), TestCancellationToken);
        var second = await Store.ClaimAsync(Claim(job.Name) with { WorkerId = "worker-2" }, TestCancellationToken);

        Assert.NotNull(second);
        Assert.Equal(2, second.Attempt);
        Assert.NotEqual(first.ClaimToken, second.ClaimToken);
        Assert.Equal("worker-2", second.WorkerId);
    }

    [Fact]
    public async Task Renew_ExtendsTheLeaseAndReportsCancellation()
    {
        var job = NewJob();
        await Store.CreateAsync(job, TestCancellationToken);
        var claimed = (await Store.ClaimAsync(Claim(job.Name, lease: ShortLease), TestCancellationToken))!;

        Assert.Equal(JobLeaseStatus.Renewed, await Store.RenewAsync(job.Id, claimed.ClaimToken!, TimeSpan.FromMinutes(1), TestCancellationToken));
        await Task.Delay(ShortLease + TimeSpan.FromMilliseconds(250), TestCancellationToken);
        Assert.Null(await Store.ClaimAsync(Claim(job.Name), TestCancellationToken));

        Assert.True(await Store.RequestCancellationAsync(job.Id, TimeSpan.FromMinutes(1), TestCancellationToken));
        Assert.Equal(JobLeaseStatus.CancellationRequested, await Store.RenewAsync(job.Id, claimed.ClaimToken!, TimeSpan.FromMinutes(1), TestCancellationToken));
        Assert.True((await Store.GetAsync(job.Id, TestCancellationToken))!.CancellationRequested);
        Assert.Equal(JobLeaseStatus.Lost, await Store.RenewAsync(job.Id, "not-the-token", TimeSpan.FromMinutes(1), TestCancellationToken));
    }

    [Fact]
    public async Task Renew_AfterTheLeaseExpiresButBeforeAReclaim_KeepsTheJob()
    {
        var job = NewJob();
        await Store.CreateAsync(job, TestCancellationToken);
        var claimed = (await Store.ClaimAsync(Claim(job.Name, lease: ShortLease), TestCancellationToken))!;
        await Task.Delay(ShortLease + TimeSpan.FromMilliseconds(250), TestCancellationToken);

        Assert.Equal(JobLeaseStatus.Renewed, await Store.RenewAsync(job.Id, claimed.ClaimToken!, TimeSpan.FromMinutes(1), TestCancellationToken));
        Assert.Null(await Store.ClaimAsync(Claim(job.Name), TestCancellationToken));
    }

    [Fact]
    public async Task Complete_Succeeded_RecordsTheResultAndClearsTheClaim()
    {
        var job = NewJob();
        await Store.CreateAsync(job, TestCancellationToken);
        var claimed = (await Store.ClaimAsync(Claim(job.Name), TestCancellationToken))!;

        Assert.True(await Store.CompleteAsync(job.Id, claimed.ClaimToken!, JobCompletion.Succeeded(TimeSpan.FromMinutes(5), "all done"), TestCancellationToken));

        var stored = (await Store.GetAsync(job.Id, TestCancellationToken))!;
        Assert.Equal(JobStatus.Succeeded, stored.Status);
        Assert.Equal("all done", stored.Message);
        Assert.NotNull(stored.CompletedAt);
        Assert.Null(stored.ClaimToken);
        Assert.False(await Store.CompleteAsync(job.Id, claimed.ClaimToken!, JobCompletion.Failed(TimeSpan.FromMinutes(5), "late"), TestCancellationToken));
        Assert.Equal(JobStatus.Succeeded, (await Store.GetAsync(job.Id, TestCancellationToken))!.Status);
    }

    [Fact]
    public async Task Complete_IsFencedByTheClaimTokenNotTheLease()
    {
        var reclaimed = NewJob();
        var expired = NewJob();
        await Store.CreateAsync(reclaimed, TestCancellationToken);
        await Store.CreateAsync(expired, TestCancellationToken);
        var first = (await Store.ClaimAsync(Claim(reclaimed.Name, lease: ShortLease), TestCancellationToken))!;
        var lapsed = (await Store.ClaimAsync(Claim(expired.Name, lease: ShortLease), TestCancellationToken))!;
        await Task.Delay(ShortLease + TimeSpan.FromMilliseconds(250), TestCancellationToken);
        var second = (await Store.ClaimAsync(Claim(reclaimed.Name), TestCancellationToken))!;

        Assert.False(await Store.CompleteAsync(reclaimed.Id, first.ClaimToken!, JobCompletion.Succeeded(TimeSpan.FromMinutes(5)), TestCancellationToken));
        Assert.True(await Store.CompleteAsync(reclaimed.Id, second.ClaimToken!, JobCompletion.Succeeded(TimeSpan.FromMinutes(5)), TestCancellationToken));
        Assert.True(await Store.CompleteAsync(expired.Id, lapsed.ClaimToken!, JobCompletion.Succeeded(TimeSpan.FromMinutes(5)), TestCancellationToken));
        Assert.Equal(JobStatus.Succeeded, (await Store.GetAsync(expired.Id, TestCancellationToken))!.Status);
    }

    [Fact]
    public async Task Complete_Retry_RequeuesAfterTheDelayAndKeepsTheError()
    {
        var job = NewJob();
        await Store.CreateAsync(job, TestCancellationToken);
        var claimed = (await Store.ClaimAsync(Claim(job.Name), TestCancellationToken))!;

        Assert.True(await Store.CompleteAsync(job.Id, claimed.ClaimToken!, JobCompletion.Retry(ShortLease, "transient"), TestCancellationToken));

        var stored = (await Store.GetAsync(job.Id, TestCancellationToken))!;
        Assert.Equal(JobStatus.Queued, stored.Status);
        Assert.Equal(1, stored.Attempt);
        Assert.Equal("transient", stored.Error);
        Assert.Null(await Store.ClaimAsync(Claim(job.Name), TestCancellationToken));

        await Task.Delay(ShortLease + TimeSpan.FromMilliseconds(250), TestCancellationToken);
        Assert.Equal(2, (await Store.ClaimAsync(Claim(job.Name), TestCancellationToken))?.Attempt);
    }

    [Fact]
    public async Task Complete_Release_RefundsTheAttemptAndRequeuesImmediately()
    {
        var job = NewJob();
        await Store.CreateAsync(job, TestCancellationToken);
        var claimed = (await Store.ClaimAsync(Claim(job.Name), TestCancellationToken))!;

        Assert.True(await Store.CompleteAsync(job.Id, claimed.ClaimToken!, JobCompletion.Release(), TestCancellationToken));

        var again = await Store.ClaimAsync(Claim(job.Name), TestCancellationToken);
        Assert.NotNull(again);
        Assert.Equal(1, again.Attempt);
    }

    [Fact]
    public async Task ReportProgress_IsRecordedOnlyForTheCurrentClaim()
    {
        var job = NewJob();
        await Store.CreateAsync(job, TestCancellationToken);
        var claimed = (await Store.ClaimAsync(Claim(job.Name), TestCancellationToken))!;

        Assert.True(await Store.ReportProgressAsync(job.Id, claimed.ClaimToken!, 40, "reading", TestCancellationToken));
        Assert.True(await Store.ReportProgressAsync(job.Id, claimed.ClaimToken!, 60, null, TestCancellationToken));
        Assert.False(await Store.ReportProgressAsync(job.Id, "not-the-token", 90, "nope", TestCancellationToken));

        var stored = (await Store.GetAsync(job.Id, TestCancellationToken))!;
        Assert.Equal(60, stored.Progress);
        Assert.Equal("reading", stored.Message);
    }

    [Fact]
    public async Task RequestCancellation_CancelsQueuedJobsAndFlagsRunningOnes()
    {
        var queued = NewJob();
        var running = NewJob();
        await Store.CreateAsync(queued, TestCancellationToken);
        await Store.CreateAsync(running, TestCancellationToken);
        await Store.ClaimAsync(Claim(running.Name), TestCancellationToken);

        Assert.True(await Store.RequestCancellationAsync(queued.Id, TimeSpan.FromMinutes(5), TestCancellationToken));
        Assert.True(await Store.RequestCancellationAsync(running.Id, TimeSpan.FromMinutes(5), TestCancellationToken));

        var cancelled = (await Store.GetAsync(queued.Id, TestCancellationToken))!;
        Assert.Equal(JobStatus.Cancelled, cancelled.Status);
        Assert.NotNull(cancelled.CompletedAt);
        Assert.Null(await Store.ClaimAsync(Claim(queued.Name), TestCancellationToken));
        var flagged = (await Store.GetAsync(running.Id, TestCancellationToken))!;
        Assert.Equal(JobStatus.Running, flagged.Status);
        Assert.True(flagged.CancellationRequested);

        Assert.False(await Store.RequestCancellationAsync(queued.Id, TimeSpan.FromMinutes(5), TestCancellationToken));
        Assert.False(await Store.RequestCancellationAsync(Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(5), TestCancellationToken));
    }

    [Fact]
    public async Task QueryAndCount_FilterByStatusAndName_NewestFirst()
    {
        string name = NewName();
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        var jobs = Enumerable.Range(0, 5).Select(i => NewJob(name) with { CreatedAt = start.AddSeconds(i) }).ToList();
        foreach (var job in jobs)
            await Store.CreateAsync(job, TestCancellationToken);
        await Store.CreateAsync(NewJob(), TestCancellationToken);

        var pages = new List<JobPage>();
        string? continuation = null;
        do
        {
            var page = await Store.QueryAsync(new JobQuery { Name = name, Limit = 2, ContinuationToken = continuation }, TestCancellationToken);
            pages.Add(page);
            continuation = page.ContinuationToken;
        } while (continuation is not null);

        Assert.Equal(jobs.Select(j => j.Id).Reverse(), pages.SelectMany(p => p.Jobs).Select(j => j.Id));
        Assert.Equal(3, pages.Count);

        await Store.ClaimAsync(Claim(name), TestCancellationToken);
        Assert.Equal(5, await Store.CountAsync(name: name, cancellationToken: TestCancellationToken));
        Assert.Equal(4, await Store.CountAsync(JobStatus.Queued, name, TestCancellationToken));
        Assert.Equal(1, await Store.CountAsync(JobStatus.Running, name, TestCancellationToken));
        Assert.Single((await Store.QueryAsync(new JobQuery { Name = name, Status = JobStatus.Running }, TestCancellationToken)).Jobs);
    }

    [Fact]
    public async Task FinishedJobs_ExpireAfterTheirRetention()
    {
        var job = NewJob();
        await Store.CreateAsync(job, TestCancellationToken);
        var claimed = (await Store.ClaimAsync(Claim(job.Name), TestCancellationToken))!;
        await Store.CompleteAsync(job.Id, claimed.ClaimToken!, JobCompletion.Succeeded(ShortLease), TestCancellationToken);
        Assert.NotNull(await Store.GetAsync(job.Id, TestCancellationToken));

        await Task.Delay(ShortLease + TimeSpan.FromSeconds(1), TestCancellationToken);

        Assert.Null(await Store.GetAsync(job.Id, TestCancellationToken));
        Assert.Equal(0, await Store.CountAsync(name: job.Name, cancellationToken: TestCancellationToken));
        Assert.True(await Store.CreateAsync(job, TestCancellationToken));
    }

    [Fact]
    public async Task Delete_RemovesTheJob()
    {
        var job = NewJob();
        await Store.CreateAsync(job, TestCancellationToken);

        Assert.True(await Store.DeleteAsync(job.Id, TestCancellationToken));
        Assert.Null(await Store.GetAsync(job.Id, TestCancellationToken));
        Assert.False(await Store.DeleteAsync(job.Id, TestCancellationToken));
    }

    [Fact]
    public async Task Delete_ARunningJob_FencesItsWorker()
    {
        var job = NewJob();
        await Store.CreateAsync(job, TestCancellationToken);
        var claimed = (await Store.ClaimAsync(Claim(job.Name), TestCancellationToken))!;

        Assert.True(await Store.DeleteAsync(job.Id, TestCancellationToken));

        Assert.Equal(JobLeaseStatus.Lost, await Store.RenewAsync(job.Id, claimed.ClaimToken!, TimeSpan.FromMinutes(1), TestCancellationToken));
        Assert.False(await Store.ReportProgressAsync(job.Id, claimed.ClaimToken!, 50, null, TestCancellationToken));
        Assert.False(await Store.CompleteAsync(job.Id, claimed.ClaimToken!, JobCompletion.Succeeded(TimeSpan.FromMinutes(1)), TestCancellationToken));
        Assert.Null(await Store.GetAsync(job.Id, TestCancellationToken));
    }

    protected static string NewName() => $"job-{Guid.NewGuid():N}"[..16];

    protected static JobInfo NewJob(string? name = null, byte[]? payload = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new JobInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name ?? NewName(),
            Payload = payload ?? [],
            CreatedAt = now,
            UpdatedAt = now,
            AvailableAt = now.AddSeconds(-1)
        };
    }

    protected static JobClaimRequest Claim(params string[] names) => Claim(names, TimeSpan.FromMinutes(1));

    protected static JobClaimRequest Claim(string name, TimeSpan lease) => Claim([name], lease);

    private static JobClaimRequest Claim(string[] names, TimeSpan lease) => new() { Names = names, WorkerId = "worker-1", LeaseDuration = lease };

    private static void AssertClose(DateTimeOffset expected, DateTimeOffset actual)
    {
        Assert.True((expected - actual).Duration() < TimeSpan.FromSeconds(1), $"Expected {expected:O}, got {actual:O}.");
    }
}
