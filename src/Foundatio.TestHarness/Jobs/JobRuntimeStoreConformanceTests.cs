using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Foundatio.Xunit;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Foundatio.Tests.Jobs;

/// <summary>
/// Shared conformance suite every <see cref="IJobRuntimeStore"/> implementation must satisfy: state round-trips,
/// optimistic-concurrency transitions, leases/claims, stale recovery (including the renew-during-reclaim race), and
/// scheduled-dispatch claiming. The in-memory reference and any real store (Redis, etc.) run the same assertions so a
/// new backend is validated against the exact behavior the runtime depends on.
/// </summary>
/// <remarks>
/// A <see cref="FakeTimeProvider"/> drives time so lease-expiry and claim-steal paths are deterministic without real
/// sleeps. <see cref="CreateStore"/> returns <c>null</c> when the backing store is unavailable (e.g. Redis not
/// configured), in which case every test skips.
/// </remarks>
public abstract class JobRuntimeStoreConformanceTests : TestWithLoggingBase
{
    [Fact]
    public virtual async Task RetentionPressure_EvictsHistoryAndPreservesIdempotencyAsync()
    {
        var time = new FakeTimeProvider();
        var store = CreateStore(time, new JobRuntimeStoreOptions { MaxActiveJobs = 1, MaxHistoryJobs = 1, MaxDeduplicationRecords = 10 });
        Assert.SkipWhen(store is null, "Job runtime store not configured.");
        var token = TestCancellationToken;
        var request = new JobClaimRequest { NodeId = "node", JobTypes = ["work"] };
        for (int i = 0; i < 3; i++)
        {
            await store.CreateIfAbsentAsync(new JobState { JobId = $"job-{i}", Name = "work", JobType = "work" }, token);
            var claim = Assert.IsType<JobState>(await store.ClaimNextAsync(request, token));
            Assert.True(await store.CompleteJobAsync(claim.JobId, claim.ClaimToken!, new JobCompletion { Kind = JobCompletionKind.Succeeded, Message = "Done" }, token));
            time.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Single(await store.QueryAsync(new JobQuery(), token));
        await store.CreateIfAbsentAsync(new JobState { JobId = "job-0", Name = "work", JobType = "work" }, token);
        Assert.Null(await store.ClaimNextAsync(request, token));
        Assert.Equal(new JobRuntimeStoreStats(0, 1, 3, 0), await store.GetStatsAsync(token));
        time.Advance(TimeSpan.FromDays(8));
        await store.CleanupAsync(cancellationToken: token);
        await store.CreateIfAbsentAsync(new JobState { JobId = "job-0", Name = "work", JobType = "work" }, token);
        Assert.NotNull(await store.ClaimNextAsync(request, token));
    }

    [Fact]
    public virtual async Task RetryPolicy_PersistsDelayAndHonorsTerminalFailureAsync()
    {
        var time = new FakeTimeProvider();
        var store = CreateStore(time);
        Assert.SkipWhen(store is null, "Job runtime store not configured.");
        var token = TestCancellationToken;
        await store.CreateIfAbsentAsync(new JobState
        {
            JobId = "retry-policy",
            Name = "work",
            JobType = "work",
            MaxAttempts = 5,
            RetryPolicy = new JobRetryPolicy { InitialDelay = TimeSpan.FromMinutes(2), MaxDelay = TimeSpan.FromMinutes(3), JitterFactor = 0 }
        }, token);
        var request = new JobClaimRequest { NodeId = "node", JobTypes = ["work"] };
        var claim = Assert.IsType<JobState>(await store.ClaimNextAsync(request, token));
        await store.CompleteJobAsync(claim.JobId, claim.ClaimToken!, new JobCompletion { Kind = JobCompletionKind.Failed, Error = "Transient" }, token);
        Assert.Null(await store.ClaimNextAsync(request, token));
        time.Advance(TimeSpan.FromMinutes(2));
        claim = Assert.IsType<JobState>(await store.ClaimNextAsync(request, token));
        await store.CompleteJobAsync(claim.JobId, claim.ClaimToken!, new JobCompletion { Kind = JobCompletionKind.Failed, Retryable = false, Error = "Permanent" }, token);
        Assert.Equal(JobStatus.Failed, (await store.GetAsync(claim.JobId, token))!.Status);
        Assert.Null(await store.ClaimNextAsync(request, token));
    }

    [Fact]
    public virtual async Task DispatchCapacity_RejectsNewWorkWithoutLosingAcceptedMessagesAsync()
    {
        var time = new FakeTimeProvider();
        var store = CreateStore(time, new JobRuntimeStoreOptions { MaxScheduledDispatches = 1, MaxPayloadBytes = 32 });
        Assert.SkipWhen(store is null, "Job runtime store not configured.");
        var token = TestCancellationToken;
        var dispatch = new ScheduledDispatchState { DispatchId = "one", Body = "data"u8.ToArray(), DueUtc = time.GetUtcNow(), Destination = DestinationAddress.ForQueue("work") };
        await store.ScheduleDispatchAsync(dispatch, token);
        await Assert.ThrowsAsync<JobException>(() => store.ScheduleDispatchAsync(dispatch with { DispatchId = "two" }, token));
        var claim = Assert.Single(await store.ClaimDueDispatchesAsync(time.GetUtcNow(), 1, "claim", TimeSpan.FromMinutes(1), token));
        Assert.Equal("one", claim.DispatchId);
        Assert.True(await store.CompleteDispatchAsync(claim.DispatchId, "claim", token));
        await store.ScheduleDispatchAsync(dispatch with { DispatchId = "two" }, token);
        await Assert.ThrowsAsync<JobException>(() => store.CreateIfAbsentAsync(new JobState { JobId = "large", Name = "large", Payload = new byte[33] }, token));
    }

    [Fact]
    public virtual async Task PerNodeExpiry_RetiresOnlyUnclaimedOccurrencesAsync()
    {
        var time = new FakeTimeProvider();
        var store = CreateStore(time);
        Assert.SkipWhen(store is null, "Job runtime store not configured.");
        var token = TestCancellationToken;
        await store.CreateIfAbsentAsync(new JobState { JobId = "retired", Name = "work", JobType = "work", RequiredNodeId = "retired-node", ExpiresUtc = time.GetUtcNow().AddMinutes(1) }, token);
        await store.CreateIfAbsentAsync(new JobState { JobId = "normal", Name = "work", JobType = "work" }, token);
        time.Advance(TimeSpan.FromDays(2));
        await store.CleanupAsync(cancellationToken: token);
        Assert.Equal(JobStatus.Cancelled, (await store.GetAsync("retired", token))!.Status);
        Assert.Equal(JobStatus.Queued, (await store.GetAsync("normal", token))!.Status);
    }

    [Fact]
    public virtual async Task DispatchLease_UsesAcquisitionClockRatherThanDueCutoffAsync()
    {
        var time = new FakeTimeProvider();
        var store = CreateStore(time);
        Assert.SkipWhen(store is null, "Job runtime store not configured.");
        var token = TestCancellationToken;
        var cutoff = time.GetUtcNow();
        await store.ScheduleDispatchAsync(new ScheduledDispatchState { DispatchId = "old", Body = "data"u8.ToArray(), DueUtc = cutoff }, token);
        time.Advance(TimeSpan.FromMinutes(2));
        var claim = Assert.Single(await store.ClaimDueDispatchesAsync(cutoff, 1, "owner", TimeSpan.FromMinutes(1), token));
        Assert.Equal(time.GetUtcNow().AddMinutes(1), claim.ClaimExpiresUtc);
        Assert.True(await store.CompleteDispatchAsync(claim.DispatchId, "owner", token));
        Assert.False(await store.CompleteDispatchAsync(claim.DispatchId, "owner", token));
    }

    [Fact]
    public virtual async Task CreateIfAbsentAsync_UnspecifiedTimestamps_UsesStoreClockAsync()
    {
        var time = new FakeTimeProvider();
        var store = CreateStore(time);
        Assert.SkipWhen(store is null, "Store unavailable");
        var token = TestCancellationToken;
        await store.CreateIfAbsentAsync(new JobState { JobId = "clock", Name = "work", JobType = "work.v1" }, token);
        var state = await store.GetAsync("clock", token);
        Assert.NotNull(state);
        Assert.Equal(time.GetUtcNow(), state.CreatedUtc);
        Assert.Equal(time.GetUtcNow(), state.LastUpdatedUtc);
        Assert.NotNull(await store.ClaimJobAsync("clock", new JobClaimRequest { NodeId = "node", JobTypes = ["work.v1"] }, token));
    }

    [Fact]
    public virtual async Task Schedules_PageByNameWithoutLoadingOtherDefinitionsAsync()
    {
        var store = CreateStore(new FakeTimeProvider());
        Assert.SkipWhen(store is null, "Store unavailable");
        var token = TestContext.Current.CancellationToken;
        foreach (var name in new[] { "e", "a", "d", "b", "c" })
            await store.ScheduleAsync(new ScheduledJobDefinition { Name = name, Cron = "0 0 * * *", JobType = "work.v1" }, token);
        var first = await store.GetSchedulesAsync(new ScheduleQuery { Limit = 2 }, token);
        var second = await store.GetSchedulesAsync(new ScheduleQuery { Limit = 2, AfterName = first[^1].Name }, token);
        var third = await store.GetSchedulesAsync(new ScheduleQuery { Limit = 2, AfterName = second[^1].Name }, token);
        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, first.Concat(second).Concat(third).Select(d => d.Name));
    }

    [Fact]
    public virtual async Task Schedules_ReconciliationPreservesEditsAndRejectsStaleWritersAsync()
    {
        var store = CreateStore(new FakeTimeProvider());
        Assert.SkipWhen(store is null, "Store unavailable");
        var schedules = Assert.IsAssignableFrom<IScheduledJobStore>(store);
        var token = TestContext.Current.CancellationToken;
        var declared = new ScheduledJobDefinition { Name = "nightly", Cron = "0 3 * * *", JobType = "work.v1", ConfigurationVersion = 1 };
        await schedules.ReconcileAsync(declared, token);
        var initial = await schedules.GetScheduleAsync("nightly", token);
        Assert.NotNull(initial);
        Assert.Equal(1, initial.Revision);
        await schedules.ScheduleAsync(initial with { Enabled = false, Cron = "0 4 * * *" }, token);
        await schedules.ReconcileAsync(declared, token);
        var edited = await schedules.GetScheduleAsync("nightly", token);
        Assert.NotNull(edited);
        Assert.False(edited.Enabled);
        Assert.Equal("0 4 * * *", edited.Cron);
        await Assert.ThrowsAsync<JobException>(() => schedules.ScheduleAsync(initial with { Cron = "0 5 * * *" }, token));
        await Assert.ThrowsAsync<JobException>(() => schedules.ReconcileAsync(declared with { Cron = "0 6 * * *" }, token));
        await schedules.ReconcileAsync(declared with { Cron = "0 6 * * *", ConfigurationVersion = 2 }, token);
        await schedules.ReconcileAsync(declared, token);
        var latest = await schedules.GetScheduleAsync("nightly", token);
        Assert.NotNull(latest);
        Assert.Equal("0 6 * * *", latest.Cron);
        Assert.True(latest.Enabled);
        Assert.Equal(2, latest.ConfigurationVersion);
        Assert.Equal(3, latest.Revision);
    }

    [Fact]
    public virtual async Task ScheduledDispatches_LeasedHeadDoesNotHideEligibleWorkAsync()
    {
        var time = new FakeTimeProvider();
        var store = CreateStore(time);
        Assert.SkipWhen(store is null, "Store unavailable");
        var token = TestContext.Current.CancellationToken;
        await store.ScheduleDispatchAsync(new ScheduledDispatchState
        {
            DispatchId = "first",
            Destination = DestinationAddress.ForQueue("work"),
            Body = ReadOnlyMemory<byte>.Empty,
            DueUtc = time.GetUtcNow()
        }, token);
        Assert.Single(await store.ClaimDueDispatchesAsync(time.GetUtcNow(), 1, "first-claim", TimeSpan.FromMinutes(1), token));
        await store.ScheduleDispatchAsync(new ScheduledDispatchState
        {
            DispatchId = "second",
            Destination = DestinationAddress.ForQueue("work"),
            Body = ReadOnlyMemory<byte>.Empty,
            DueUtc = time.GetUtcNow()
        }, token);
        Assert.Equal("second", Assert.Single(await store.ClaimDueDispatchesAsync(time.GetUtcNow(), 1, "second-claim", TimeSpan.FromMinutes(1), token)).DispatchId);

        time.Advance(TimeSpan.FromMinutes(2));
        await store.CompleteDispatchAsync("first", "first-claim", token);
        var reclaimed = await store.ClaimDueDispatchesAsync(time.GetUtcNow(), 10, "fresh-claim", TimeSpan.FromMinutes(1), token);
        Assert.Equal(2, reclaimed.Count);
        await store.ReleaseDispatchAsync("first", "first-claim", time.GetUtcNow().AddDays(1), token);
        await store.CompleteDispatchAsync("first", "first-claim", token);
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(2, (await store.ClaimDueDispatchesAsync(time.GetUtcNow(), 10, "another-claim", TimeSpan.FromMinutes(1), token)).Count);
    }

    [Fact]
    public virtual async Task CreateOccurrenceAsync_AtomicallyPreventsOverlapAndHonorsNodeAffinityAsync()
    {
        var time = new FakeTimeProvider();
        var store = CreateStore(time);
        if (store is null)
        {
            Assert.Skip("Job runtime store not configured.");
            return;
        }

        var token = TestCancellationToken;
        var occurrence = NewJob(time, "occurrence-1") with { JobType = "work.v1", ScheduleName = "periodic", RequiredNodeId = "node-a" };
        var creates = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => store.CreateOccurrenceAsync(occurrence with { JobId = $"occurrence-{i}" }, cancellationToken: token)));
        Assert.Single(creates.Where(created => created == JobOccurrenceResult.Created));
        var request = new JobClaimRequest { NodeId = "node-b", JobTypes = new[] { "work.v1" } };
        Assert.Null(await store.ClaimNextAsync(request, token));
        var claimed = await store.ClaimNextAsync(request with { NodeId = "node-a" }, token);
        Assert.NotNull(claimed);
        Assert.True(await store.CompleteJobAsync(claimed.JobId, claimed.ClaimToken!, new JobCompletion { Kind = JobCompletionKind.Succeeded }, token));
        Assert.Equal(JobOccurrenceResult.Created, await store.CreateOccurrenceAsync(occurrence with { JobId = "next-occurrence" }, cancellationToken: token));
    }

    [Fact]
    public virtual async Task ClaimNextAsync_FiltersEligibilityAndFencesRepeatedWorkerIdentityAsync()
    {
        var time = new FakeTimeProvider();
        var store = CreateStore(time);
        if (store is null)
        {
            Assert.Skip("Job runtime store not configured.");
            return;
        }

        var token = TestCancellationToken;
        await store.CreateIfAbsentAsync(NewJob(time, "other") with { JobType = "other.v1" }, token);
        await store.CreateIfAbsentAsync(NewJob(time, "eligible") with { JobType = "work.v1" }, token);
        var request = new JobClaimRequest { NodeId = "same-node", JobTypes = new[] { "work.v1" }, Lease = TimeSpan.FromSeconds(10) };
        var first = await store.ClaimNextAsync(request, token);
        Assert.NotNull(first);
        Assert.Equal("eligible", first.JobId);
        Assert.NotEmpty(first.ClaimToken!);
        Assert.Equal(1, first.Attempt);
        Assert.Null(await store.ClaimNextAsync(request, token));

        time.Advance(TimeSpan.FromSeconds(11));
        var second = await store.ClaimNextAsync(request, token);
        Assert.NotNull(second);
        Assert.NotEqual(first.ClaimToken, second.ClaimToken);
        Assert.Equal(2, second.Attempt);
        Assert.False(await store.CompleteJobAsync(first.JobId, first.ClaimToken!, new JobCompletion { Kind = JobCompletionKind.Succeeded }, token));
        Assert.False(await store.RenewJobLeaseAsync(first.JobId, first.ClaimToken!, request.Lease, token));
        Assert.False(await store.ReportJobProgressAsync(first.JobId, first.ClaimToken!, 99, "stale", token));
        Assert.True(await store.CompleteJobAsync(second.JobId, second.ClaimToken!, new JobCompletion { Kind = JobCompletionKind.Succeeded }, token));
        Assert.Equal(JobStatus.Completed, (await store.GetAsync("eligible", token))!.Status);
        Assert.Equal(JobStatus.Queued, (await store.GetAsync("other", token))!.Status);
    }

    [Fact]
    public virtual async Task CompleteJobAsync_FailurePersistsRetryAvailabilityAndBudgetAsync()
    {
        var time = new FakeTimeProvider();
        var store = CreateStore(time);
        if (store is null)
        {
            Assert.Skip("Job runtime store not configured.");
            return;
        }

        var token = TestCancellationToken;
        await store.CreateIfAbsentAsync(NewJob(time, "retry") with { JobType = "work.v1", MaxAttempts = 2 }, token);
        var request = new JobClaimRequest { NodeId = "worker", JobTypes = new[] { "work.v1" } };
        var first = await store.ClaimNextAsync(request, token);
        Assert.NotNull(first);
        Assert.True(await store.CompleteJobAsync(first.JobId, first.ClaimToken!, new JobCompletion { Kind = JobCompletionKind.Failed, Error = "temporary" }, token));
        Assert.Null(await store.ClaimNextAsync(request, token));
        var pending = await store.GetAsync(first.JobId, token);
        Assert.NotNull(pending);
        Assert.Equal(JobStatus.Queued, pending.Status);
        Assert.NotNull(pending.AvailableUtc);
        Assert.Null(pending.CompletedUtc);
        time.Advance(pending.AvailableUtc.Value - time.GetUtcNow());
        var second = await store.ClaimNextAsync(request, token);
        Assert.NotNull(second);
        Assert.True(await store.CompleteJobAsync(second.JobId, second.ClaimToken!, new JobCompletion { Kind = JobCompletionKind.Failed, Error = "permanent" }, token));
        Assert.Equal(JobStatus.Failed, (await store.GetAsync(second.JobId, token))!.Status);
        Assert.Null(await store.ClaimNextAsync(request, token));
    }

    protected JobRuntimeStoreConformanceTests(ITestOutputHelper output) : base(output) { }

    /// <summary>Creates a fresh, isolated store bound to <paramref name="timeProvider"/>, or null when unavailable.</summary>
    protected abstract IJobRuntimeStore? CreateStore(TimeProvider timeProvider, JobRuntimeStoreOptions? options = null);

    protected static JobState NewJob(TimeProvider time, string id, string name = "conformance-job", JobStatus status = JobStatus.Queued)
    {
        var now = time.GetUtcNow();
        return new JobState { JobId = id, Name = name, JobType = "work.v1", Status = status, CreatedUtc = now, LastUpdatedUtc = now };
    }

    [Fact]
    public virtual async Task JobLifecycle_RoundTripsAndTransitionsAsync()
    {
        var time = new FakeTimeProvider();
        var store = CreateStore(time);
        if (store is null)
        {
            Assert.Skip("Job runtime store not configured.");
            return;
        }

        var ct = TestCancellationToken;
        var created = time.GetUtcNow();

        // Create with a rich, fully-populated state and assert every field survives the round-trip.
        var job = NewJob(time, "job-1", "emailer") with
        {
            JobType = "Acme.EmailJob",
            Payload = new byte[] { 1, 2, 3, 4 },
            PayloadType = "Acme.EmailJobArgs",
            Progress = 10,
            ProgressMessage = "starting",
            Attempt = 1,
            ScheduledForUtc = created.AddMinutes(1),
            AvailableUtc = created.AddMinutes(1)
        };
        await store.CreateIfAbsentAsync(job, ct);

        var got = await store.GetAsync("job-1", ct);
        Assert.NotNull(got);
        Assert.Equal("emailer", got.Name);
        Assert.Equal("Acme.EmailJob", got.JobType);
        Assert.NotNull(got.Payload);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, got.Payload.Value.ToArray());
        Assert.Equal("Acme.EmailJobArgs", got.PayloadType);
        Assert.Equal(JobStatus.Queued, got.Status);
        Assert.Equal(10, got.Progress);
        Assert.Equal("starting", got.ProgressMessage);
        Assert.Equal(1, got.Attempt);
        Assert.Equal(created, got.CreatedUtc);
        Assert.Equal(created.AddMinutes(1), got.ScheduledForUtc);

        // Create-if-absent is a no-op once the row exists: a second create must not overwrite.
        await store.CreateIfAbsentAsync(job with { Name = "overwritten" }, ct);
        Assert.Equal("emailer", (await store.GetAsync("job-1", ct))!.Name);

        var request = new JobClaimRequest { NodeId = "node-a", JobTypes = new[] { "Acme.EmailJob" } };
        Assert.Null(await store.ClaimJobAsync("job-1", request, ct));
        time.Advance(TimeSpan.FromMinutes(1));
        var claimed = await store.ClaimJobAsync("job-1", request, ct);
        Assert.NotNull(claimed);
        Assert.Equal(JobStatus.Processing, claimed.Status);
        Assert.Equal("node-a", claimed.NodeId);
        Assert.Equal(time.GetUtcNow().AddMinutes(5), claimed.LeaseExpiresUtc);
        Assert.Equal(2, claimed.Attempt);
        Assert.Equal(time.GetUtcNow(), claimed.StartedUtc);
        Assert.True(await store.ReportJobProgressAsync("job-1", claimed.ClaimToken!, 55, "halfway", ct));
        got = await store.GetAsync("job-1", ct);
        Assert.Equal(55, got!.Progress);
        Assert.Equal("halfway", got.ProgressMessage);
        Assert.False(await store.CompleteJobAsync("job-1", "wrong-claim", new JobCompletion { Kind = JobCompletionKind.Succeeded }, ct));
        Assert.True(await store.CompleteJobAsync("job-1", claimed.ClaimToken!, new JobCompletion { Kind = JobCompletionKind.Succeeded }, ct));
        got = await store.GetAsync("job-1", ct);
        Assert.Equal(JobStatus.Completed, got!.Status);
        Assert.Null(got.NodeId);
        Assert.Null(got.ClaimToken);
        Assert.Null(got.LeaseExpiresUtc);
        Assert.Equal(time.GetUtcNow(), got.CompletedUtc);
        Assert.False(await store.ReportJobProgressAsync("job-1", claimed.ClaimToken!, 12, "late", ct));
        Assert.False(await store.RenewJobLeaseAsync("job-1", claimed.ClaimToken!, request.Lease, ct));

        await store.CreateIfAbsentAsync(NewJob(time, "job-2", "worker"), ct);
        Assert.False(await store.IsCancellationRequestedAsync("job-2", ct));
        Assert.True(await store.RequestCancellationAsync("job-2", ct));
        Assert.True(await store.IsCancellationRequestedAsync("job-2", ct));

        // Operating on a missing job is a benign no-op (returns false / does not throw).
        Assert.False(await store.RequestCancellationAsync("missing", ct));
        Assert.Null(await store.GetAsync("missing", ct));
    }

    [Fact]
    public virtual async Task Query_FiltersByNameStatusAndLimitAsync()
    {
        var time = new FakeTimeProvider();
        var store = CreateStore(time);
        if (store is null)
        {
            Assert.Skip("Job runtime store not configured.");
            return;
        }

        var ct = TestCancellationToken;
        var t = time.GetUtcNow();

        // Monitoring uses stable ID ordering, independent of execution progress updates.
        await store.CreateIfAbsentAsync(NewJob(time, "a", "alpha", JobStatus.Queued) with { LastUpdatedUtc = t }, ct);
        await store.CreateIfAbsentAsync(NewJob(time, "b", "alpha", JobStatus.Processing) with { LastUpdatedUtc = t.AddSeconds(1) }, ct);
        await store.CreateIfAbsentAsync(NewJob(time, "c", "beta", JobStatus.Queued) with { LastUpdatedUtc = t.AddSeconds(2) }, ct);

        var byName = await store.QueryAsync(new JobQuery { Name = "alpha" }, ct);
        Assert.Equal(["a", "b"], byName.Select(j => j.JobId));

        var byStatus = await store.QueryAsync(new JobQuery { Status = JobStatus.Queued }, ct);
        Assert.Equal(new HashSet<string> { "a", "c" }, byStatus.Select(j => j.JobId).ToHashSet());

        var byBoth = await store.QueryAsync(new JobQuery { Name = "alpha", Status = JobStatus.Queued }, ct);
        Assert.Equal("a", Assert.Single(byBoth).JobId);

        var all = await store.QueryAsync(new JobQuery(), ct);
        Assert.Equal(new HashSet<string> { "a", "b", "c" }, all.Select(j => j.JobId).ToHashSet());

        // Continue with the returned cursor and the same filters.
        var limited = await store.QueryAsync(new JobQuery { Limit = 1 }, ct);
        Assert.Equal("a", Assert.Single(limited).JobId);
        var next = await store.QueryAsync(new JobQuery { Limit = 1, AfterJobId = limited.ContinuationToken }, ct);
        Assert.Equal("b", Assert.Single(next).JobId);


    }

    [Fact]
    public virtual async Task CleanupAsync_OnlyRemovesExpiredTerminalJobsAsync()
    {
        var time = new FakeTimeProvider();
        var store = CreateStore(time);
        Assert.SkipWhen(store is null, "Store unavailable");
        var token = TestCancellationToken;
        await store.CreateIfAbsentAsync(NewJob(time, "completed"), token);
        await store.CreateIfAbsentAsync(NewJob(time, "cancelled"), token);
        await store.CreateIfAbsentAsync(NewJob(time, "queued"), token);
        var claim = await store.ClaimJobAsync("completed", new JobClaimRequest { NodeId = "node", JobTypes = ["work.v1"] }, token);
        Assert.NotNull(claim);
        await store.CompleteJobAsync("completed", claim.ClaimToken!, new JobCompletion { Kind = JobCompletionKind.Succeeded }, token);
        await store.RequestCancellationAsync("cancelled", token);
        time.Advance(TimeSpan.FromDays(6));
        Assert.Equal(0, await store.CleanupAsync(cancellationToken: token));
        time.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, await store.CleanupAsync(1, token));
        Assert.Equal(1, await store.CleanupAsync(1, token));
        Assert.Equal("queued", Assert.Single(await store.QueryAsync(new JobQuery(), token)).JobId);
        Assert.NotNull(await store.ClaimJobAsync("queued", new JobClaimRequest { NodeId = "node", JobTypes = ["work.v1"] }, token));
    }

    [Fact]
    public virtual async Task Leasing_RenewalPreventsRecoveryAndInterruptionReturnsWorkAsync()
    {
        var time = new FakeTimeProvider();
        var store = CreateStore(time);
        Assert.SkipWhen(store is null, "Store unavailable");
        var token = TestContext.Current.CancellationToken;
        await store.CreateIfAbsentAsync(NewJob(time, "job-1"), token);
        var request = new JobClaimRequest { NodeId = "node-a", JobTypes = new[] { "work.v1" }, Lease = TimeSpan.FromMinutes(1) };
        var first = await store.ClaimJobAsync("job-1", request, token);
        Assert.NotNull(first);
        Assert.Null(await store.ClaimJobAsync("job-1", request, token));
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(await store.RenewJobLeaseAsync("job-1", first.ClaimToken!, request.Lease, token));
        time.Advance(TimeSpan.FromSeconds(40));
        Assert.Null(await store.ClaimJobAsync("job-1", request with { NodeId = "node-b" }, token));
        Assert.True(await store.CompleteJobAsync("job-1", first.ClaimToken!, new JobCompletion { Kind = JobCompletionKind.Interrupted }, token));
        var second = await store.ClaimJobAsync("job-1", request with { NodeId = "node-b" }, token);
        Assert.NotNull(second);
        Assert.NotEqual(first.ClaimToken, second.ClaimToken);
        Assert.Equal("node-b", second.NodeId);
        Assert.Equal(2, second.Attempt);
        Assert.False(await store.CompleteJobAsync("job-1", first.ClaimToken!, new JobCompletion { Kind = JobCompletionKind.Succeeded }, token));
    }

    [Fact]
    public virtual async Task ScheduledDispatches_ClaimCompleteAndRescheduleAsync()
    {
        var time = new FakeTimeProvider();
        var store = CreateStore(time);
        if (store is null)
        {
            Assert.Skip("Job runtime store not configured.");
            return;
        }

        var ct = TestCancellationToken;
        var t = time.GetUtcNow();

        var headers = MessageHeaders.Create(new Dictionary<string, string> { ["message.type"] = "order.created", ["tenant"] = "acme" });
        var options = new TransportSendOptions { Priority = MessagePriority.High };
        byte[] body = [0x01, 0x02, 0xFF, 0x00, 0x10];

        var due = new ScheduledDispatchState
        {
            DispatchId = "d1",
            Kind = ScheduledDispatchKind.QueueMessage,
            Destination = DestinationAddress.ForQueue("jobs"),
            Body = body,
            Headers = headers,
            Options = options,
            DueUtc = t.AddMinutes(-1)
        };
        var future = new ScheduledDispatchState
        {
            DispatchId = "d2",
            Kind = ScheduledDispatchKind.QueueMessage,
            Destination = DestinationAddress.ForQueue("later"),
            Body = body,
            DueUtc = t.AddHours(1)
        };
        await store.ScheduleDispatchAsync(due, ct);
        await store.ScheduleDispatchAsync(future, ct);
        // Re-scheduling the same id is a no-op (must not overwrite the dispatch).
        await store.ScheduleDispatchAsync(due with { Destination = DestinationAddress.ForQueue("overwritten") }, ct);

        // Only the due dispatch is claimed; the full payload round-trips and the attempt counter increments.
        var claimed = await store.ClaimDueDispatchesAsync(t, 100, "node-a", TimeSpan.FromMinutes(5), ct);
        var d = Assert.Single(claimed);
        Assert.Equal("d1", d.DispatchId);
        Assert.Equal(ScheduledDispatchKind.QueueMessage, d.Kind);
        Assert.Equal(DestinationAddress.ForQueue("jobs"), d.Destination);
        Assert.Equal(body, d.Body.ToArray());
        Assert.Equal("acme", d.Headers["tenant"]);
        Assert.Equal("order.created", d.Headers["message.type"]);
        Assert.Equal(MessagePriority.High, d.Options.Priority);
        Assert.Equal("node-a", d.ClaimOwner);
        Assert.Equal(1, d.Attempts);

        // A competing claim sees nothing while the lease is live (and d2 is not yet due).
        Assert.Empty(await store.ClaimDueDispatchesAsync(t, 100, "node-b", TimeSpan.FromMinutes(5), ct));

        // A complete from the wrong owner is ignored: after the lease lapses the dispatch is re-claimable, attempt 2.
        await store.CompleteDispatchAsync("d1", "node-b", ct);
        time.Advance(TimeSpan.FromMinutes(6));
        var reclaimed = await store.ClaimDueDispatchesAsync(t.AddMinutes(6), 100, "node-a", TimeSpan.FromMinutes(5), ct);
        Assert.Equal(2, Assert.Single(reclaimed).Attempts);

        // The owning node completes it for good.
        await store.CompleteDispatchAsync("d1", "node-a", ct);
        Assert.Empty(await store.ClaimDueDispatchesAsync(t.AddMinutes(12), 100, "node-a", TimeSpan.FromMinutes(5), ct));

        // Release reschedules a claimed dispatch to its next due time and clears ownership (recurring-occurrence path).
        var recurring = new ScheduledDispatchState
        {
            DispatchId = "d3",
            Kind = ScheduledDispatchKind.QueueMessage,
            Destination = DestinationAddress.ForQueue("cron"),
            Body = body,
            DueUtc = t.AddMinutes(20)
        };
        await store.ScheduleDispatchAsync(recurring, ct);
        Assert.Equal("d3", Assert.Single(await store.ClaimDueDispatchesAsync(t.AddMinutes(21), 100, "node-a", TimeSpan.FromMinutes(5), ct)).DispatchId);

        await store.ReleaseDispatchAsync("d3", "node-b", t.AddMinutes(50), ct); // wrong owner: ignored
        await store.ReleaseDispatchAsync("d3", "node-a", t.AddMinutes(50), ct);
        Assert.Empty(await store.ClaimDueDispatchesAsync(t.AddMinutes(40), 100, "node-c", TimeSpan.FromMinutes(5), ct));
        var rescheduled = Assert.Single(await store.ClaimDueDispatchesAsync(t.AddMinutes(51), 100, "node-c", TimeSpan.FromMinutes(5), ct));
        Assert.Equal("d3", rescheduled.DispatchId);
        Assert.Equal("node-c", rescheduled.ClaimOwner);
    }

    [Fact]
    public virtual async Task ClaimAsync_ExpiredUnclaimedOccurrence_RetiresBeforeExecutionAsync()
    {
        var time = new FakeTimeProvider();
        var store = CreateStore(time);
        Assert.SkipWhen(store is null, "Store unavailable");
        var token = TestCancellationToken;
        await store.CreateOccurrenceAsync(NewJob(time, "expired") with
        {
            ScheduleName = "daily",
            RequiredNodeId = "node",
            ExpiresUtc = time.GetUtcNow().AddMinutes(1)
        }, cancellationToken: token);
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(await store.ClaimJobAsync("expired", new JobClaimRequest { NodeId = "node", JobTypes = ["work.v1"] }, token));
        Assert.Equal(JobStatus.Cancelled, (await store.GetAsync("expired", token))!.Status);
    }

    [Fact]
    public virtual Task ScheduleDispatchAsync_HeadersExceedPayloadBudget_RejectsAsync()
    {
        var time = new FakeTimeProvider();
        var store = CreateStore(time, new JobRuntimeStoreOptions { MaxPayloadBytes = 16 });
        Assert.SkipWhen(store is null, "Store unavailable");
        return Assert.ThrowsAsync<JobException>(() => store.ScheduleDispatchAsync(new ScheduledDispatchState
        {
            DispatchId = "large-headers",
            Destination = DestinationAddress.ForQueue("q"),
            DueUtc = time.GetUtcNow(),
            Body = ReadOnlyMemory<byte>.Empty,
            Headers = MessageHeaders.Empty.ToBuilder().Set("evidence", new string('x', 32)).Build()
        }, TestCancellationToken));
    }

    [Fact]
    public virtual async Task Concurrency_OptimisticControlElectsSingleWinnerAsync()
    {
        var time = new FakeTimeProvider();
        var store = CreateStore(time);
        if (store is null)
        {
            Assert.Skip("Job runtime store not configured.");
            return;
        }

        var ct = TestCancellationToken;
        const int contenders = 25;

        // Many nodes race to claim the same unclaimed job: exactly one may win, and the store must agree on the owner.
        await store.CreateIfAbsentAsync(NewJob(time, "claim-race"), ct);
        var claims = await Task.WhenAll(Enumerable.Range(0, contenders)
            .Select(i => Task.Run(() => store.ClaimJobAsync("claim-race", new JobClaimRequest { NodeId = $"node-{i}", JobTypes = new[] { "work.v1" } }, ct), ct)));
        Assert.Equal(1, claims.Count(claimed => claimed is not null));
        var ownedBy = (await store.GetAsync("claim-race", ct))!.NodeId;
        Assert.StartsWith("node-", ownedBy);

        // A single due dispatch contested by many claimers must be handed to exactly one.
        await store.ScheduleDispatchAsync(new ScheduledDispatchState
        {
            DispatchId = "dispatch-race",
            Destination = DestinationAddress.ForQueue("q"),
            Body = new byte[] { 1 },
            DueUtc = time.GetUtcNow().AddMinutes(-1)
        }, ct);
        var dispatchClaims = await Task.WhenAll(Enumerable.Range(0, contenders)
            .Select(i => Task.Run(() => store.ClaimDueDispatchesAsync(time.GetUtcNow(), 100, $"node-{i}", TimeSpan.FromMinutes(5), ct), ct)));
        Assert.Equal(1, dispatchClaims.Sum(claimed => claimed.Count(d => d.DispatchId == "dispatch-race")));
    }
}
