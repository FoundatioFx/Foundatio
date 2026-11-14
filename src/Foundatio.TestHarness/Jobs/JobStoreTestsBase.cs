using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Jobs.Store;
using Foundatio.Xunit;
using Xunit;

namespace Foundatio.Tests.Jobs;

/// <summary>
/// The conformance suite every <see cref="IJobStore"/> must pass. Derive from it and implement <see cref="CreateStore"/>.
/// Tests use unique job names so they can run in parallel against a shared database. Tests that wait for a lease or
/// retention to run out poll the store until its own clock says so, so a store whose clock is a few seconds off the
/// test machine's, or whose calls are slow, still passes.
/// </summary>
/// <example>
/// <code>
/// public class RedisJobStoreTests(ITestOutputHelper output) : JobStoreTestsBase(output)
/// {
///     protected override IJobStore CreateStore() => new RedisJobStore(...);
/// }
/// </code>
/// </example>
[Experimental(ProviderApi.DiagnosticId, UrlFormat = ProviderApi.UrlFormat)]
public abstract class JobStoreTestsBase : TestWithLoggingBase
{
    /// <summary>Creates the suite, logging to <paramref name="output"/>.</summary>
    protected JobStoreTestsBase(ITestOutputHelper output) : base(output)
    {
    }

    /// <summary>Creates a store connected to the database under test.</summary>
    protected abstract IJobStore CreateStore();

    /// <summary>The lease used by tests that wait for leases to expire.</summary>
    protected virtual TimeSpan ShortLease => TimeSpan.FromSeconds(1);

    /// <summary>The store under test, created by <see cref="CreateStore"/> on first use.</summary>
    protected IJobStore Store => field ??= CreateStore();

    /// <summary>How long tests poll for a lease or retention to run out on the store's clock before failing.</summary>
    protected virtual TimeSpan ExpiryTimeout => TimeSpan.FromSeconds(15);

    /// <summary>A created job reads back queued with attempt 0 and its fields as given; an unknown id reads as null.</summary>
    [Fact]
    public async Task Create_ThenGet_ReturnsTheJob()
    {
        var job = NewJob(payload: [1, 2, 3, 0, 255]) with { CorrelationId = "correlation", ScheduleName = "nightly" };

        Assert.True(await Store.CreateAsync(job, TestCancellationToken));

        var stored = await Store.GetAsync(job.Id, TestCancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(job.Name, stored.Name);
        Assert.Equal(JobState.Queued, stored.State);
        Assert.Equal(0, stored.Attempt);
        Assert.Equal([1, 2, 3, 0, 255], stored.Payload.ToArray());
        Assert.Equal("correlation", stored.CorrelationId);
        Assert.Equal("nightly", stored.ScheduleName);
        AssertClose(job.AvailableAt, stored.AvailableAt);
        Assert.Null(await Store.GetAsync(Guid.NewGuid().ToString("N"), TestCancellationToken));
    }

    /// <summary>Creating a job whose id exists returns false and changes nothing.</summary>
    [Fact]
    public async Task Create_WithAnExistingId_ReturnsFalseAndKeepsTheOriginal()
    {
        var job = NewJob(payload: [1]);
        Assert.True(await Store.CreateAsync(job, TestCancellationToken));

        Assert.False(await Store.CreateAsync(job with { Payload = new byte[] { 2 } }, TestCancellationToken));

        Assert.Equal([1], (await Store.GetAsync(job.Id, TestCancellationToken))!.Payload.ToArray());
    }

    /// <summary>A claim takes a due job, marks it running and sets its attempt, worker, claim token and lease.</summary>
    [Fact]
    public async Task Claim_ReturnsADueJobAndMarksItRunning()
    {
        var job = NewJob();
        await Store.CreateAsync(job, TestCancellationToken);

        var claimed = await Store.ClaimAsync(Claim(job.Name, lease: TimeSpan.FromMinutes(1)), TestCancellationToken);

        Assert.NotNull(claimed);
        Assert.Equal(job.Id, claimed.Id);
        Assert.Equal(JobState.Running, claimed.State);
        Assert.Equal(1, claimed.Attempt);
        Assert.False(String.IsNullOrEmpty(claimed.ClaimToken));
        Assert.Equal("worker-1", claimed.WorkerId);
        Assert.NotNull(claimed.StartedAt);
        Assert.True(claimed.LeaseExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30));
        Assert.Equal(JobState.Running, (await Store.GetAsync(job.Id, TestCancellationToken))!.State);
        Assert.Null(await Store.ClaimAsync(Claim(job.Name), TestCancellationToken));
    }

    /// <summary>A claim skips jobs that are not due yet and jobs with names it did not ask for.</summary>
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

    /// <summary>Claims of one name return its jobs in the order they became due.</summary>
    [Fact]
    public async Task Claim_ReturnsTheJobDueLongestFirst()
    {
        string name = NewName();
        var now = DateTimeOffset.UtcNow;
        var newer = NewJob(name) with { AvailableAt = now.AddMinutes(-1) };
        var older = NewJob(name) with { AvailableAt = now.AddMinutes(-5) };
        await Store.CreateAsync(newer, TestCancellationToken);
        await Store.CreateAsync(older, TestCancellationToken);

        Assert.Equal(older.Id, (await Store.ClaimAsync(Claim(name), TestCancellationToken))?.Id);
        Assert.Equal(newer.Id, (await Store.ClaimAsync(Claim(name), TestCancellationToken))?.Id);
    }

    /// <summary>A claim of several names returns the job that has been due longest among all of them.</summary>
    [Fact]
    public async Task Claim_ReturnsTheJobDueLongestAcrossAllRequestedNames()
    {
        var now = DateTimeOffset.UtcNow;
        var recent = NewJob() with { AvailableAt = now.AddMinutes(-1) };
        var overdue = NewJob() with { AvailableAt = now.AddMinutes(-5) };
        await Store.CreateAsync(recent, TestCancellationToken);
        await Store.CreateAsync(overdue, TestCancellationToken);

        Assert.Equal(overdue.Id, (await Store.ClaimAsync(Claim(recent.Name, overdue.Name), TestCancellationToken))?.Id);
    }

    /// <summary>Concurrent claims never hand the same job to two workers.</summary>
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

    /// <summary>A job whose lease ran out is claimed again with a new claim token and the next attempt.</summary>
    [Fact]
    public async Task Claim_AfterTheLeaseExpires_ReclaimsWithANewTokenAndAttempt()
    {
        var job = NewJob();
        await Store.CreateAsync(job, TestCancellationToken);
        var first = (await Store.ClaimAsync(Claim(job.Name, lease: ShortLease), TestCancellationToken))!;
        Assert.Null(await Store.ClaimAsync(Claim(job.Name), TestCancellationToken));

        var second = await WaitForClaimAsync(Claim(job.Name) with { WorkerId = "worker-2" });

        Assert.Equal(2, second.Attempt);
        Assert.NotEqual(first.ClaimToken, second.ClaimToken);
        Assert.Equal("worker-2", second.WorkerId);
    }

    /// <summary>Renewing extends the lease, and reports when cancellation has been requested.</summary>
    [Fact]
    public async Task Renew_ExtendsTheLeaseAndReportsCancellation()
    {
        var job = NewJob();
        var control = NewJob();
        await Store.CreateAsync(job, TestCancellationToken);
        await Store.CreateAsync(control, TestCancellationToken);
        var claimed = (await Store.ClaimAsync(Claim(job.Name, lease: ShortLease), TestCancellationToken))!;
        await Store.ClaimAsync(Claim(control.Name, lease: ShortLease), TestCancellationToken);

        Assert.Equal(JobLeaseStatus.Renewed, await Store.RenewAsync(job.Id, claimed.ClaimToken!, TimeSpan.FromMinutes(1), TestCancellationToken));
        await WaitForClaimAsync(Claim(control.Name));
        Assert.Null(await Store.ClaimAsync(Claim(job.Name), TestCancellationToken));

        Assert.True(await Store.RequestCancellationAsync(job.Id, TimeSpan.FromMinutes(1), TestCancellationToken));
        Assert.Equal(JobLeaseStatus.CancellationRequested, await Store.RenewAsync(job.Id, claimed.ClaimToken!, TimeSpan.FromMinutes(1), TestCancellationToken));
        Assert.True((await Store.GetAsync(job.Id, TestCancellationToken))!.CancellationRequested);
        Assert.Equal(JobLeaseStatus.Lost, await Store.RenewAsync(job.Id, "not-the-token", TimeSpan.FromMinutes(1), TestCancellationToken));
    }

    /// <summary>A renewal that comes after the lease ran out, but before another claim, keeps the job.</summary>
    [Fact]
    public async Task Renew_AfterTheLeaseExpiresButBeforeAReclaim_KeepsTheJob()
    {
        var job = NewJob();
        var control = NewJob();
        await Store.CreateAsync(job, TestCancellationToken);
        await Store.CreateAsync(control, TestCancellationToken);
        var claimed = (await Store.ClaimAsync(Claim(job.Name, lease: ShortLease), TestCancellationToken))!;
        await Store.ClaimAsync(Claim(control.Name, lease: ShortLease), TestCancellationToken);
        await WaitForClaimAsync(Claim(control.Name));

        Assert.Equal(JobLeaseStatus.Renewed, await Store.RenewAsync(job.Id, claimed.ClaimToken!, TimeSpan.FromMinutes(1), TestCancellationToken));
        Assert.Null(await Store.ClaimAsync(Claim(job.Name), TestCancellationToken));
    }

    /// <summary>Completing as succeeded records the state, message and completion time, and clears the claim so a second completion is rejected.</summary>
    [Fact]
    public async Task Complete_Succeeded_RecordsTheResultAndClearsTheClaim()
    {
        var job = NewJob();
        await Store.CreateAsync(job, TestCancellationToken);
        var claimed = (await Store.ClaimAsync(Claim(job.Name), TestCancellationToken))!;

        Assert.True(await Store.CompleteAsync(job.Id, claimed.ClaimToken!, JobCompletion.Succeeded(TimeSpan.FromMinutes(5), "all done"), TestCancellationToken));

        var stored = (await Store.GetAsync(job.Id, TestCancellationToken))!;
        Assert.Equal(JobState.Succeeded, stored.State);
        Assert.Equal("all done", stored.Message);
        Assert.NotNull(stored.CompletedAt);
        Assert.Null(stored.ClaimToken);
        Assert.False(await Store.CompleteAsync(job.Id, claimed.ClaimToken!, JobCompletion.Failed(TimeSpan.FromMinutes(5), "late"), TestCancellationToken));
        Assert.Equal(JobState.Succeeded, (await Store.GetAsync(job.Id, TestCancellationToken))!.State);
    }

    /// <summary>Completion is accepted for the current claim token, even after its lease ran out, and rejected for a replaced one.</summary>
    [Fact]
    public async Task Complete_IsFencedByTheClaimTokenNotTheLease()
    {
        var reclaimed = NewJob();
        var expired = NewJob();
        await Store.CreateAsync(reclaimed, TestCancellationToken);
        await Store.CreateAsync(expired, TestCancellationToken);
        var lapsed = (await Store.ClaimAsync(Claim(expired.Name, lease: ShortLease), TestCancellationToken))!;
        var first = (await Store.ClaimAsync(Claim(reclaimed.Name, lease: ShortLease), TestCancellationToken))!;
        var second = await WaitForClaimAsync(Claim(reclaimed.Name));

        Assert.False(await Store.CompleteAsync(reclaimed.Id, first.ClaimToken!, JobCompletion.Succeeded(TimeSpan.FromMinutes(5)), TestCancellationToken));
        Assert.True(await Store.CompleteAsync(reclaimed.Id, second.ClaimToken!, JobCompletion.Succeeded(TimeSpan.FromMinutes(5)), TestCancellationToken));
        Assert.True(await Store.CompleteAsync(expired.Id, lapsed.ClaimToken!, JobCompletion.Succeeded(TimeSpan.FromMinutes(5)), TestCancellationToken));
        Assert.Equal(JobState.Succeeded, (await Store.GetAsync(expired.Id, TestCancellationToken))!.State);
    }

    /// <summary>A retry queues the job again once the delay has passed, keeping the attempt and the error.</summary>
    [Fact]
    public async Task Complete_Retry_RequeuesAfterTheDelayAndKeepsTheError()
    {
        var job = NewJob();
        await Store.CreateAsync(job, TestCancellationToken);
        var claimed = (await Store.ClaimAsync(Claim(job.Name), TestCancellationToken))!;

        Assert.True(await Store.CompleteAsync(job.Id, claimed.ClaimToken!, JobCompletion.Retry(ShortLease, "transient"), TestCancellationToken));

        var stored = (await Store.GetAsync(job.Id, TestCancellationToken))!;
        Assert.Equal(JobState.Queued, stored.State);
        Assert.Equal(1, stored.Attempt);
        Assert.Equal("transient", stored.Error);
        Assert.Null(await Store.ClaimAsync(Claim(job.Name), TestCancellationToken));

        Assert.Equal(2, (await WaitForClaimAsync(Claim(job.Name))).Attempt);
    }

    /// <summary>A release queues the job again at once without counting the attempt.</summary>
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

    /// <summary>Progress is recorded for the current claim token and rejected for any other.</summary>
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

    /// <summary>Requesting cancellation cancels a queued job and flags a running one for its worker; it returns false for a finished or unknown job.</summary>
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
        Assert.Equal(JobState.Cancelled, cancelled.State);
        Assert.NotNull(cancelled.CompletedAt);
        Assert.Null(await Store.ClaimAsync(Claim(queued.Name), TestCancellationToken));
        var flagged = (await Store.GetAsync(running.Id, TestCancellationToken))!;
        Assert.Equal(JobState.Running, flagged.State);
        Assert.True(flagged.CancellationRequested);

        Assert.False(await Store.RequestCancellationAsync(queued.Id, TimeSpan.FromMinutes(5), TestCancellationToken));
        Assert.False(await Store.RequestCancellationAsync(Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(5), TestCancellationToken));
    }

    /// <summary>Queries and counts filter by state and name, and queries page through jobs newest first.</summary>
    [Fact]
    public async Task QueryAndCount_FilterByStatusAndName_NewestFirst()
    {
        string name = NewName();
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        var jobs = Enumerable.Range(0, 5).Select(i => NewJob(name) with { CreatedAt = start.AddSeconds(i) }).ToList();
        foreach (var job in jobs)
            await Store.CreateAsync(job, TestCancellationToken);
        await Store.CreateAsync(NewJob(), TestCancellationToken);

        var pages = new List<JobPage<JobInfo>>();
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
        Assert.Equal(4, await Store.CountAsync(JobState.Queued, name, TestCancellationToken));
        Assert.Equal(1, await Store.CountAsync(JobState.Running, name, TestCancellationToken));
        Assert.Single((await Store.QueryAsync(new JobQuery { Name = name, State = JobState.Running }, TestCancellationToken)).Jobs);
    }

    /// <summary>A finished job disappears once its retention has passed.</summary>
    [Fact]
    public async Task FinishedJobs_ExpireAfterTheirRetention()
    {
        var job = NewJob();
        await Store.CreateAsync(job, TestCancellationToken);
        var claimed = (await Store.ClaimAsync(Claim(job.Name), TestCancellationToken))!;
        await Store.CompleteAsync(job.Id, claimed.ClaimToken!, JobCompletion.Succeeded(ShortLease), TestCancellationToken);
        Assert.NotNull(await Store.GetAsync(job.Id, TestCancellationToken));

        await WaitUntilAsync(async () => await Store.GetAsync(job.Id, TestCancellationToken) is null);

        Assert.Equal(0, await Store.CountAsync(name: job.Name, cancellationToken: TestCancellationToken));
        Assert.Equal(0, await Store.CountAsync(JobState.Succeeded, job.Name, TestCancellationToken));
        Assert.True(await Store.CreateAsync(job, TestCancellationToken));
    }

    /// <summary>Claiming and retrying a job keep the fields they do not change, such as the payload, correlation id and schedule.</summary>
    [Fact]
    public async Task ClaimAndRetry_KeepTheJobsOtherFields()
    {
        var createdAt = DateTimeOffset.UtcNow.AddHours(-1);
        var job = NewJob(payload: [9, 8, 7]) with { CorrelationId = "correlation", ScheduleName = "hourly", CreatedAt = createdAt };
        await Store.CreateAsync(job, TestCancellationToken);

        var claimed = (await Store.ClaimAsync(Claim(job.Name), TestCancellationToken))!;
        AssertKeptFields(claimed);

        Assert.True(await Store.CompleteAsync(job.Id, claimed.ClaimToken!, JobCompletion.Retry(TimeSpan.Zero, "transient"), TestCancellationToken));
        var retried = (await Store.GetAsync(job.Id, TestCancellationToken))!;
        AssertKeptFields(retried);
        Assert.Equal(claimed.StartedAt, retried.StartedAt);
        Assert.Null(retried.ClaimToken);
        Assert.Null(retried.WorkerId);
        Assert.Null(retried.LeaseExpiresAt);

        AssertKeptFields(await WaitForClaimAsync(Claim(job.Name)));

        void AssertKeptFields(JobInfo stored)
        {
            Assert.Equal(job.Name, stored.Name);
            Assert.Equal([9, 8, 7], stored.Payload.ToArray());
            Assert.Equal("correlation", stored.CorrelationId);
            Assert.Equal("hourly", stored.ScheduleName);
            AssertClose(createdAt, stored.CreatedAt);
        }
    }

    /// <summary>Every header round-trips exactly, through reads, claims and retries.</summary>
    [Fact]
    public async Task Create_WithHeaders_KeepsThemThroughGetClaimAndRetry()
    {
        var headers = new Dictionary<string, string>
        {
            ["traceparent"] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            ["tracestate"] = "vendor=1,other=2",
            ["tenant:id"] = "acme / ünïcode",
            ["Tenant:Id"] = "case matters",
            ["empty"] = ""
        };
        var job = NewJob() with { Headers = headers };
        var plain = NewJob();
        await Store.CreateAsync(job, TestCancellationToken);
        await Store.CreateAsync(plain, TestCancellationToken);

        AssertHeaders((await Store.GetAsync(job.Id, TestCancellationToken))!);
        var claimed = (await Store.ClaimAsync(Claim(job.Name), TestCancellationToken))!;
        AssertHeaders(claimed);
        Assert.True(await Store.CompleteAsync(job.Id, claimed.ClaimToken!, JobCompletion.Retry(TimeSpan.Zero, "transient"), TestCancellationToken));
        AssertHeaders((await Store.GetAsync(job.Id, TestCancellationToken))!);
        AssertHeaders(await WaitForClaimAsync(Claim(job.Name)));

        Assert.Empty((await Store.GetAsync(plain.Id, TestCancellationToken))!.Headers);
        Assert.Empty((await Store.ClaimAsync(Claim(plain.Name), TestCancellationToken))!.Headers);

        void AssertHeaders(JobInfo stored)
        {
            Assert.Equal(headers.OrderBy(h => h.Key, StringComparer.Ordinal), stored.Headers.OrderBy(h => h.Key, StringComparer.Ordinal));
        }
    }

    /// <summary>A succeeded job loses its payload and keeps the rest of its record; failed and cancelled jobs keep their payload.</summary>
    [Fact]
    public async Task Complete_Succeeded_DropsThePayloadAndKeepsTheRestWhileFailedAndCancelledJobsKeepTheirs()
    {
        var succeeded = NewJob(payload: [1, 2, 3]) with { CorrelationId = "correlation", ScheduleName = "nightly", Headers = new Dictionary<string, string> { ["tenant"] = "acme" } };
        var failed = NewJob(payload: [4, 5]);
        var cancelled = NewJob(payload: [6]);
        var cancelledWhileQueued = NewJob(payload: [7]);
        foreach (var job in new[] { succeeded, failed, cancelled, cancelledWhileQueued })
            await Store.CreateAsync(job, TestCancellationToken);

        await ClaimAndCompleteAsync(succeeded, JobCompletion.Succeeded(TimeSpan.FromMinutes(5), "done"));
        await ClaimAndCompleteAsync(failed, JobCompletion.Failed(TimeSpan.FromMinutes(5), "boom"));
        await ClaimAndCompleteAsync(cancelled, JobCompletion.Cancelled(TimeSpan.FromMinutes(5)));
        Assert.True(await Store.RequestCancellationAsync(cancelledWhileQueued.Id, TimeSpan.FromMinutes(5), TestCancellationToken));

        var stored = (await Store.GetAsync(succeeded.Id, TestCancellationToken))!;
        Assert.True(stored.Payload.IsEmpty);
        Assert.Equal(JobState.Succeeded, stored.State);
        Assert.Equal(succeeded.Name, stored.Name);
        Assert.Equal(1, stored.Attempt);
        Assert.Equal("done", stored.Message);
        Assert.Equal("correlation", stored.CorrelationId);
        Assert.Equal("nightly", stored.ScheduleName);
        Assert.Equal("acme", Assert.Single(stored.Headers).Value);
        Assert.NotNull(stored.StartedAt);
        Assert.NotNull(stored.CompletedAt);
        Assert.True(Assert.Single((await Store.QueryAsync(new JobQuery { Name = succeeded.Name }, TestCancellationToken)).Jobs).Payload.IsEmpty);
        Assert.Equal([4, 5], (await Store.GetAsync(failed.Id, TestCancellationToken))!.Payload.ToArray());
        Assert.Equal([6], (await Store.GetAsync(cancelled.Id, TestCancellationToken))!.Payload.ToArray());
        Assert.Equal([7], (await Store.GetAsync(cancelledWhileQueued.Id, TestCancellationToken))!.Payload.ToArray());

        async Task ClaimAndCompleteAsync(JobInfo job, JobCompletion completion)
        {
            var claimed = (await Store.ClaimAsync(Claim(job.Name), TestCancellationToken))!;
            Assert.True(await Store.CompleteAsync(job.Id, claimed.ClaimToken!, completion, TestCancellationToken));
        }
    }

    /// <summary>Completing as failed or cancelled records the state, error or message and completion time, and clears the claim.</summary>
    [Fact]
    public async Task Complete_FailedAndCancelled_RecordTheirStatusAndClearTheClaim()
    {
        var failed = NewJob();
        var cancelled = NewJob();
        await Store.CreateAsync(failed, TestCancellationToken);
        await Store.CreateAsync(cancelled, TestCancellationToken);
        var failedClaim = (await Store.ClaimAsync(Claim(failed.Name), TestCancellationToken))!;
        var cancelledClaim = (await Store.ClaimAsync(Claim(cancelled.Name), TestCancellationToken))!;

        Assert.True(await Store.CompleteAsync(failed.Id, failedClaim.ClaimToken!, JobCompletion.Failed(TimeSpan.FromMinutes(5), "boom"), TestCancellationToken));
        Assert.True(await Store.CompleteAsync(cancelled.Id, cancelledClaim.ClaimToken!, JobCompletion.Cancelled(TimeSpan.FromMinutes(5), "stopped"), TestCancellationToken));

        var storedFailure = (await Store.GetAsync(failed.Id, TestCancellationToken))!;
        Assert.Equal(JobState.Failed, storedFailure.State);
        Assert.Equal("boom", storedFailure.Error);
        Assert.Equal(1, storedFailure.Attempt);
        AssertFinished(storedFailure);
        var storedCancellation = (await Store.GetAsync(cancelled.Id, TestCancellationToken))!;
        Assert.Equal(JobState.Cancelled, storedCancellation.State);
        Assert.Equal("stopped", storedCancellation.Message);
        AssertFinished(storedCancellation);
        Assert.Null(await Store.ClaimAsync(Claim(failed.Name, cancelled.Name), TestCancellationToken));

        static void AssertFinished(JobInfo stored)
        {
            Assert.NotNull(stored.CompletedAt);
            Assert.Null(stored.ClaimToken);
            Assert.Null(stored.WorkerId);
            Assert.Null(stored.LeaseExpiresAt);
        }
    }

    /// <summary>A success after a failed attempt clears the error.</summary>
    [Fact]
    public async Task Complete_SucceededAfterARetry_ClearsTheError()
    {
        var job = NewJob();
        await Store.CreateAsync(job, TestCancellationToken);
        var first = (await Store.ClaimAsync(Claim(job.Name), TestCancellationToken))!;
        await Store.CompleteAsync(job.Id, first.ClaimToken!, JobCompletion.Retry(TimeSpan.Zero, "transient"), TestCancellationToken);
        var second = await WaitForClaimAsync(Claim(job.Name));

        Assert.True(await Store.CompleteAsync(job.Id, second.ClaimToken!, JobCompletion.Succeeded(TimeSpan.FromMinutes(5)), TestCancellationToken));

        var stored = (await Store.GetAsync(job.Id, TestCancellationToken))!;
        Assert.Equal(JobState.Succeeded, stored.State);
        Assert.Null(stored.Error);
        Assert.Equal(2, stored.Attempt);
    }

    /// <summary>A completion racing a claim of the same expired lease either completes the job or loses it to the claim, never both.</summary>
    [Fact]
    public Task Complete_RacingAReclaim_EitherCompletesOrIsReclaimed()
    {
        return Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            var job = NewJob();
            await Store.CreateAsync(job, TestCancellationToken);
            var claimed = (await Store.ClaimAsync(Claim(job.Name, lease: TimeSpan.FromMilliseconds(1)), TestCancellationToken))!;
            await Task.Delay(TimeSpan.FromMilliseconds(20), TestCancellationToken);

            var complete = Store.CompleteAsync(job.Id, claimed.ClaimToken!, JobCompletion.Succeeded(TimeSpan.FromMinutes(5)), TestCancellationToken);
            var reclaim = Store.ClaimAsync(Claim(job.Name), TestCancellationToken);
            bool completed = await complete;
            var reclaimed = await reclaim;

            Assert.NotEqual(completed, reclaimed is not null);
            Assert.Equal(completed ? JobState.Succeeded : JobState.Running, (await Store.GetAsync(job.Id, TestCancellationToken))?.State);
        }));
    }

    /// <summary>Renewals and progress reports with a replaced claim token are rejected.</summary>
    [Fact]
    public async Task RenewAndReportProgress_WithATokenFromBeforeAReclaim_AreRejected()
    {
        var job = NewJob();
        await Store.CreateAsync(job, TestCancellationToken);
        var first = (await Store.ClaimAsync(Claim(job.Name, lease: ShortLease), TestCancellationToken))!;
        var second = await WaitForClaimAsync(Claim(job.Name) with { WorkerId = "worker-2" });

        Assert.Equal(JobLeaseStatus.Lost, await Store.RenewAsync(job.Id, first.ClaimToken!, TimeSpan.FromMinutes(1), TestCancellationToken));
        Assert.False(await Store.ReportProgressAsync(job.Id, first.ClaimToken!, 90, "stale", TestCancellationToken));

        var stored = (await Store.GetAsync(job.Id, TestCancellationToken))!;
        Assert.Equal(second.ClaimToken, stored.ClaimToken);
        Assert.Equal("worker-2", stored.WorkerId);
        Assert.Null(stored.Progress);
        Assert.Null(stored.Message);
        Assert.Equal(JobLeaseStatus.Renewed, await Store.RenewAsync(job.Id, second.ClaimToken!, TimeSpan.FromMinutes(1), TestCancellationToken));
    }

    /// <summary>A job reclaimed after its lease ran out keeps its cancellation request.</summary>
    [Fact]
    public async Task Claim_AfterTheLeaseExpires_KeepsTheCancellationRequest()
    {
        var job = NewJob();
        await Store.CreateAsync(job, TestCancellationToken);
        await Store.ClaimAsync(Claim(job.Name, lease: ShortLease), TestCancellationToken);
        Assert.True(await Store.RequestCancellationAsync(job.Id, TimeSpan.FromMinutes(5), TestCancellationToken));

        var reclaimed = await WaitForClaimAsync(Claim(job.Name));

        Assert.True(reclaimed.CancellationRequested);
        Assert.Equal(JobLeaseStatus.CancellationRequested, await Store.RenewAsync(job.Id, reclaimed.ClaimToken!, TimeSpan.FromMinutes(1), TestCancellationToken));
    }

    /// <summary>A queued job that was cancelled disappears after the retention passed with the request, and its id can be reused.</summary>
    [Fact]
    public async Task RequestCancellation_OfAQueuedJob_ExpiresAfterTheRetention()
    {
        var job = NewJob();
        await Store.CreateAsync(job, TestCancellationToken);

        Assert.True(await Store.RequestCancellationAsync(job.Id, ShortLease, TestCancellationToken));
        Assert.Equal(JobState.Cancelled, (await Store.GetAsync(job.Id, TestCancellationToken))?.State);
        await WaitUntilAsync(async () => await Store.GetAsync(job.Id, TestCancellationToken) is null);

        Assert.Equal(0, await Store.CountAsync(JobState.Cancelled, job.Name, TestCancellationToken));
        Assert.Empty((await Store.QueryAsync(new JobQuery { Name = job.Name }, TestCancellationToken)).Jobs);
        Assert.True(await Store.CreateAsync(job, TestCancellationToken));
    }

    /// <summary>Queries and counts by state find succeeded, failed and cancelled jobs under their own state.</summary>
    [Fact]
    public async Task QueryAndCount_FinishedAndCancelledJobs_FilterByTheirStatus()
    {
        string name = NewName();
        var now = DateTimeOffset.UtcNow;
        var succeeded = NewJob(name) with { AvailableAt = now.AddMinutes(-4) };
        var failed = NewJob(name) with { AvailableAt = now.AddMinutes(-3) };
        var cancelledWhileRunning = NewJob(name) with { AvailableAt = now.AddMinutes(-2) };
        var cancelledWhileQueued = NewJob(name);
        foreach (var job in new[] { succeeded, failed, cancelledWhileRunning, cancelledWhileQueued })
            await Store.CreateAsync(job, TestCancellationToken);

        Assert.True(await Store.RequestCancellationAsync(cancelledWhileQueued.Id, TimeSpan.FromMinutes(5), TestCancellationToken));
        var retain = TimeSpan.FromMinutes(5);
        foreach (var (job, completion) in new[] { (succeeded, JobCompletion.Succeeded(retain)), (failed, JobCompletion.Failed(retain, "boom")), (cancelledWhileRunning, JobCompletion.Cancelled(retain)) })
        {
            var claimed = (await Store.ClaimAsync(Claim(name), TestCancellationToken))!;
            Assert.Equal(job.Id, claimed.Id);
            Assert.True(await Store.CompleteAsync(claimed.Id, claimed.ClaimToken!, completion, TestCancellationToken));
        }

        Assert.Equal(0, await Store.CountAsync(JobState.Queued, name, TestCancellationToken));
        Assert.Equal(0, await Store.CountAsync(JobState.Running, name, TestCancellationToken));
        Assert.Equal(1, await Store.CountAsync(JobState.Succeeded, name, TestCancellationToken));
        Assert.Equal(1, await Store.CountAsync(JobState.Failed, name, TestCancellationToken));
        Assert.Equal(2, await Store.CountAsync(JobState.Cancelled, name, TestCancellationToken));
        Assert.Equal(4, await Store.CountAsync(name: name, cancellationToken: TestCancellationToken));

        Assert.Equal([succeeded.Id], await QueryIdsAsync(JobState.Succeeded));
        Assert.Equal([failed.Id], await QueryIdsAsync(JobState.Failed));
        Assert.Equal(new[] { cancelledWhileRunning.Id, cancelledWhileQueued.Id }.Order(), await QueryIdsAsync(JobState.Cancelled));
        Assert.Empty(await QueryIdsAsync(JobState.Queued));
        Assert.Contains(failed.Id, (await Store.QueryAsync(new JobQuery { State = JobState.Failed, Limit = 1000 }, TestCancellationToken)).Jobs.Select(j => j.Id));

        async Task<string[]> QueryIdsAsync(JobState status) =>
            (await Store.QueryAsync(new JobQuery { Name = name, State = status }, TestCancellationToken)).Jobs.Select(j => j.Id).Order().ToArray();
    }

    /// <summary>Every operation throws <see cref="ArgumentException"/> for a missing job id or claim token, or an invalid claim request.</summary>
    [Fact]
    public async Task Operations_WithAMissingIdOrToken_ThrowArgumentException()
    {
        var lease = TimeSpan.FromMinutes(1);
        var done = JobCompletion.Succeeded(lease);
        foreach (string? missing in new[] { null, "", " " })
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => Store.GetAsync(missing!, TestCancellationToken));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => Store.RenewAsync(missing!, "token", lease, TestCancellationToken));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => Store.RenewAsync("job", missing!, lease, TestCancellationToken));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => Store.ReportProgressAsync(missing!, "token", 50, null, TestCancellationToken));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => Store.ReportProgressAsync("job", missing!, 50, null, TestCancellationToken));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => Store.CompleteAsync(missing!, "token", done, TestCancellationToken));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => Store.CompleteAsync("job", missing!, done, TestCancellationToken));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => Store.RequestCancellationAsync(missing!, lease, TestCancellationToken));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => Store.DeleteAsync(missing!, TestCancellationToken));
        }

        await Assert.ThrowsAnyAsync<ArgumentException>(() => Store.CompleteAsync("job", "token", null!, TestCancellationToken));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Store.ClaimAsync(new JobClaimRequest { Names = [NewName()], WorkerId = " ", LeaseDuration = lease }, TestCancellationToken));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Store.ClaimAsync(new JobClaimRequest { Names = [NewName()], WorkerId = "worker-1", LeaseDuration = TimeSpan.Zero }, TestCancellationToken));
    }

    /// <summary>A deleted job can no longer be read, and deleting it again returns false.</summary>
    [Fact]
    public async Task Delete_RemovesTheJob()
    {
        var job = NewJob();
        await Store.CreateAsync(job, TestCancellationToken);

        Assert.True(await Store.DeleteAsync(job.Id, TestCancellationToken));
        Assert.Null(await Store.GetAsync(job.Id, TestCancellationToken));
        Assert.False(await Store.DeleteAsync(job.Id, TestCancellationToken));
    }

    /// <summary>Deleting a running job rejects its worker's later renewals, progress reports and completion.</summary>
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

    /// <summary>A job name no other test uses.</summary>
    protected static string NewName() => $"job-{Guid.NewGuid():N}"[..16];

    /// <summary>A job that is already due, named <paramref name="name"/> or a new name, with <paramref name="payload"/> or an empty one.</summary>
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
            AvailableAt = now.AddMinutes(-1)
        };
    }

    /// <summary>A claim of <paramref name="names"/> with a one-minute lease.</summary>
    protected static JobClaimRequest Claim(params string[] names) => Claim(names, TimeSpan.FromMinutes(1));

    /// <summary>A claim of <paramref name="name"/> with the given lease.</summary>
    protected static JobClaimRequest Claim(string name, TimeSpan lease) => Claim([name], lease);

    private static JobClaimRequest Claim(string[] names, TimeSpan lease) => new() { Names = names, WorkerId = "worker-1", LeaseDuration = lease };

    /// <summary>Claims a job once the store considers one due, polling until <see cref="ExpiryTimeout"/>.</summary>
    protected async Task<JobInfo> WaitForClaimAsync(JobClaimRequest request)
    {
        JobInfo? claimed = null;
        await WaitUntilAsync(async () => (claimed = await Store.ClaimAsync(request, TestCancellationToken)) is not null);
        return claimed!;
    }

    /// <summary>Polls <paramref name="condition"/> until it holds, failing after <see cref="ExpiryTimeout"/>.</summary>
    protected async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        long started = Stopwatch.GetTimestamp();
        while (!await condition())
        {
            if (Stopwatch.GetElapsedTime(started) > ExpiryTimeout)
                Assert.Fail($"The store did not reach the expected state within {ExpiryTimeout}.");

            await Task.Delay(TimeSpan.FromMilliseconds(50), TestCancellationToken);
        }
    }

    private static void AssertClose(DateTimeOffset expected, DateTimeOffset actual)
    {
        Assert.True((expected - actual).Duration() < TimeSpan.FromSeconds(1), $"Expected {expected:O}, got {actual:O}.");
    }
}
