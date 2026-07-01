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
    protected JobRuntimeStoreConformanceTests(ITestOutputHelper output) : base(output) { }

    /// <summary>Creates a fresh, isolated store bound to <paramref name="timeProvider"/>, or null when unavailable.</summary>
    protected abstract IJobRuntimeStore? CreateStore(TimeProvider timeProvider);

    protected static JobState NewJob(TimeProvider time, string id, string name = "conformance-job", JobStatus status = JobStatus.Queued)
    {
        var now = time.GetUtcNow();
        return new JobState { JobId = id, Name = name, Status = status, CreatedUtc = now, LastUpdatedUtc = now };
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
            Progress = 10,
            ProgressMessage = "starting",
            Attempt = 1,
            ScheduledForUtc = created.AddMinutes(1)
        };
        await store.CreateIfAbsentAsync(job, ct);

        var got = await store.GetAsync("job-1", ct);
        Assert.NotNull(got);
        Assert.Equal("emailer", got.Name);
        Assert.Equal("Acme.EmailJob", got.JobType);
        Assert.Equal(JobStatus.Queued, got.Status);
        Assert.Equal(10, got.Progress);
        Assert.Equal("starting", got.ProgressMessage);
        Assert.Equal(1, got.Attempt);
        Assert.Equal(created, got.CreatedUtc);
        Assert.Equal(created.AddMinutes(1), got.ScheduledForUtc);

        // Create-if-absent is a no-op once the row exists: a second create must not overwrite.
        await store.CreateIfAbsentAsync(job with { Name = "overwritten" }, ct);
        Assert.Equal("emailer", (await store.GetAsync("job-1", ct))!.Name);

        // A transition from the wrong current status must fail and leave state untouched.
        Assert.False(await store.TryTransitionAsync("job-1", JobStatus.Processing, JobStatus.Completed, cancellationToken: ct));
        Assert.Equal(JobStatus.Queued, (await store.GetAsync("job-1", ct))!.Status);

        // Happy-path transition applies the patch atomically (status + node + lease + started + attempt delta).
        var lease = time.GetUtcNow().AddMinutes(5);
        Assert.True(await store.TryTransitionAsync("job-1", JobStatus.Queued, JobStatus.Processing,
            new JobStatePatch { NodeId = "node-a", LeaseExpiresUtc = lease, StartedUtc = created, AttemptDelta = 1 }, cancellationToken: ct));
        got = await store.GetAsync("job-1", ct);
        Assert.Equal(JobStatus.Processing, got!.Status);
        Assert.Equal("node-a", got.NodeId);
        Assert.Equal(lease, got.LeaseExpiresUtc);
        Assert.Equal(2, got.Attempt);
        Assert.Equal(created, got.StartedUtc);

        // expectedNodeId guards the transition: a stale worker (wrong node) cannot overwrite the owner's state.
        Assert.False(await store.TryTransitionAsync("job-1", JobStatus.Processing, JobStatus.Completed, expectedNodeId: "node-b", cancellationToken: ct));
        Assert.Equal(JobStatus.Processing, (await store.GetAsync("job-1", ct))!.Status);

        // Correct owner completes and clears the lease/node.
        var completedAt = time.GetUtcNow();
        Assert.True(await store.TryTransitionAsync("job-1", JobStatus.Processing, JobStatus.Completed,
            new JobStatePatch { ClearNodeId = true, ClearLeaseExpiresUtc = true, CompletedUtc = completedAt }, expectedNodeId: "node-a", cancellationToken: ct));
        got = await store.GetAsync("job-1", ct);
        Assert.Equal(JobStatus.Completed, got!.Status);
        Assert.Null(got.NodeId);
        Assert.Null(got.LeaseExpiresUtc);
        Assert.Equal(completedAt, got.CompletedUtc);

        // Progress, attempt, and cancellation are independent of transitions.
        await store.CreateIfAbsentAsync(NewJob(time, "job-2", "worker"), ct);
        await store.SetProgressAsync("job-2", 55, "halfway", ct);
        await store.IncrementAttemptAsync("job-2", ct);
        got = await store.GetAsync("job-2", ct);
        Assert.Equal(55, got!.Progress);
        Assert.Equal("halfway", got.ProgressMessage);
        Assert.Equal(1, got.Attempt);

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

        // Distinct LastUpdatedUtc values make the default newest-first ordering (and limit) deterministic.
        await store.CreateIfAbsentAsync(NewJob(time, "a", "alpha", JobStatus.Queued) with { LastUpdatedUtc = t }, ct);
        await store.CreateIfAbsentAsync(NewJob(time, "b", "alpha", JobStatus.Processing) with { LastUpdatedUtc = t.AddSeconds(1) }, ct);
        await store.CreateIfAbsentAsync(NewJob(time, "c", "beta", JobStatus.Queued) with { LastUpdatedUtc = t.AddSeconds(2) }, ct);

        var byName = await store.QueryAsync(new JobQuery { Name = "alpha" }, ct);
        Assert.Equal(["b", "a"], byName.Select(j => j.JobId));

        var byStatus = await store.QueryAsync(new JobQuery { Status = JobStatus.Queued }, ct);
        Assert.Equal(new HashSet<string> { "a", "c" }, byStatus.Select(j => j.JobId).ToHashSet());

        var byBoth = await store.QueryAsync(new JobQuery { Name = "alpha", Status = JobStatus.Queued }, ct);
        Assert.Equal("a", Assert.Single(byBoth).JobId);

        var all = await store.QueryAsync(new JobQuery(), ct);
        Assert.Equal(new HashSet<string> { "a", "b", "c" }, all.Select(j => j.JobId).ToHashSet());

        // Limit is honored against the newest-first ordering, so the most recently updated row wins.
        var limited = await store.QueryAsync(new JobQuery { Limit = 1 }, ct);
        Assert.Equal("c", Assert.Single(limited).JobId);

        // ExcludeOccurrences filters out CRON occurrences (ScheduledForUtc set) so the generic worker's Queued query
        // never claims scheduler-owned jobs.
        await store.CreateIfAbsentAsync(NewJob(time, "d", "alpha", JobStatus.Queued) with { LastUpdatedUtc = t.AddSeconds(3), ScheduledForUtc = t }, ct);
        var adHocQueued = await store.QueryAsync(new JobQuery { Status = JobStatus.Queued, ExcludeOccurrences = true }, ct);
        Assert.Equal(new HashSet<string> { "a", "c" }, adHocQueued.Select(j => j.JobId).ToHashSet()); // "d" excluded (occurrence)
        var adHocAlpha = await store.QueryAsync(new JobQuery { Name = "alpha", ExcludeOccurrences = true }, ct);
        Assert.Equal(new HashSet<string> { "a", "b" }, adHocAlpha.Select(j => j.JobId).ToHashSet()); // "d" excluded (occurrence)
    }

    [Fact]
    public virtual async Task Leasing_ClaimRenewReleaseAndStealAsync()
    {
        var time = new FakeTimeProvider();
        var store = CreateStore(time);
        if (store is null)
        {
            Assert.Skip("Job runtime store not configured.");
            return;
        }

        var ct = TestCancellationToken;
        await store.CreateIfAbsentAsync(NewJob(time, "job-1"), ct);

        var claimedAt = time.GetUtcNow();
        Assert.True(await store.TryClaimAsync("job-1", "node-a", TimeSpan.FromMinutes(5), ct));
        var got = await store.GetAsync("job-1", ct);
        Assert.Equal("node-a", got!.NodeId);
        Assert.Equal(claimedAt.AddMinutes(5), got.LeaseExpiresUtc);

        // The current owner can re-claim/renew; a different node cannot while the lease is live.
        Assert.True(await store.TryClaimAsync("job-1", "node-a", TimeSpan.FromMinutes(5), ct));
        Assert.False(await store.TryClaimAsync("job-1", "node-b", TimeSpan.FromMinutes(5), ct));

        // RenewClaim is owner-scoped.
        Assert.False(await store.RenewClaimAsync("job-1", "node-b", TimeSpan.FromMinutes(10), ct));
        Assert.True(await store.RenewClaimAsync("job-1", "node-a", TimeSpan.FromMinutes(10), ct));
        Assert.Equal(time.GetUtcNow().AddMinutes(10), (await store.GetAsync("job-1", ct))!.LeaseExpiresUtc);

        // A renewed lease is not stealable: after the lease would have lapsed the owner renews, so a competing steal
        // must fail rather than act on a stale expired-lease observation (the steal CAS must see the renew → no double-run).
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.True(await store.RenewClaimAsync("job-1", "node-a", TimeSpan.FromMinutes(10), ct));
        Assert.False(await store.TryClaimAsync("job-1", "node-b", TimeSpan.FromMinutes(5), ct));
        Assert.Equal("node-a", (await store.GetAsync("job-1", ct))!.NodeId);

        // Once the renewed lease itself lapses, another node may steal the claim.
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.True(await store.TryClaimAsync("job-1", "node-b", TimeSpan.FromMinutes(5), ct));
        Assert.Equal("node-b", (await store.GetAsync("job-1", ct))!.NodeId);

        // Release is owner-scoped and clears the lease.
        Assert.False(await store.ReleaseClaimAsync("job-1", "node-a", ct));
        Assert.True(await store.ReleaseClaimAsync("job-1", "node-b", ct));
        got = await store.GetAsync("job-1", ct);
        Assert.Null(got!.NodeId);
        Assert.Null(got.LeaseExpiresUtc);
    }

    [Fact]
    public virtual async Task StaleRecovery_ReclaimsExpiredButNotLiveOrCronAsync()
    {
        var time = new FakeTimeProvider();
        var store = CreateStore(time);
        if (store is null)
        {
            Assert.Skip("Job runtime store not configured.");
            return;
        }

        var ct = TestCancellationToken;
        var now = time.GetUtcNow();

        JobState Processing(string id, DateTimeOffset lease, string node = "node-a", DateTimeOffset? scheduledFor = null) =>
            NewJob(time, id, "worker", JobStatus.Processing) with { NodeId = node, LeaseExpiresUtc = lease, ScheduledForUtc = scheduledFor };

        await store.CreateIfAbsentAsync(Processing("plain", now.AddMinutes(-1)), ct);
        await store.CreateIfAbsentAsync(Processing("cron", now.AddMinutes(-1), scheduledFor: now), ct);
        await store.CreateIfAbsentAsync(Processing("live", now.AddMinutes(10)), ct);

        // Only the plain expired job is recoverable: the live lease and the CRON occurrence are excluded.
        var expired = await store.GetExpiredProcessingAsync(now, 100, ct);
        Assert.Equal("plain", Assert.Single(expired).JobId);

        // Reclaim re-queues it (still owned by node-a, lease still expired).
        Assert.True(await store.TryReclaimExpiredAsync("plain", now, "node-a", JobStatus.Queued,
            new JobStatePatch { ClearNodeId = true, ClearLeaseExpiresUtc = true, AttemptDelta = 1 }, ct));
        var got = await store.GetAsync("plain", ct);
        Assert.Equal(JobStatus.Queued, got!.Status);
        Assert.Null(got.NodeId);
        Assert.Equal(1, got.Attempt);

        // Renew-during-reclaim race: a job whose owner renewed since the scan must NOT be reclaimed (lease no longer expired).
        await store.CreateIfAbsentAsync(Processing("renewed", now.AddMinutes(-1)), ct);
        Assert.True(await store.RenewClaimAsync("renewed", "node-a", TimeSpan.FromMinutes(10), ct));
        Assert.False(await store.TryReclaimExpiredAsync("renewed", now, "node-a", JobStatus.Queued, cancellationToken: ct));
        Assert.Equal(JobStatus.Processing, (await store.GetAsync("renewed", ct))!.Status);

        // Owner mismatch since the scan also blocks the reclaim.
        await store.CreateIfAbsentAsync(Processing("reowned", now.AddMinutes(-1), node: "node-b"), ct);
        Assert.False(await store.TryReclaimExpiredAsync("reowned", now, "node-a", JobStatus.Queued, cancellationToken: ct));
        Assert.Equal("node-b", (await store.GetAsync("reowned", ct))!.NodeId);
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
        var options = new TransportSendOptions { DestinationRole = DestinationRole.Topic, Priority = MessagePriority.High };
        byte[] body = [0x01, 0x02, 0xFF, 0x00, 0x10];

        var due = new ScheduledDispatchState
        {
            DispatchId = "d1",
            Kind = ScheduledDispatchKind.JobOccurrence,
            Destination = "jobs",
            Body = body,
            Headers = headers,
            Options = options,
            DueUtc = t.AddMinutes(-1),
            JobId = "job-x"
        };
        var future = new ScheduledDispatchState
        {
            DispatchId = "d2",
            Kind = ScheduledDispatchKind.QueueMessage,
            Destination = "later",
            Body = body,
            DueUtc = t.AddHours(1)
        };
        await store.ScheduleDispatchAsync(due, ct);
        await store.ScheduleDispatchAsync(future, ct);
        // Re-scheduling the same id is a no-op (must not overwrite the destination).
        await store.ScheduleDispatchAsync(due with { Destination = "overwritten" }, ct);

        // Only the due dispatch is claimed; the full payload round-trips and the attempt counter increments.
        var claimed = await store.ClaimDueDispatchesAsync(t, 100, "node-a", TimeSpan.FromMinutes(5), ct);
        var d = Assert.Single(claimed);
        Assert.Equal("d1", d.DispatchId);
        Assert.Equal(ScheduledDispatchKind.JobOccurrence, d.Kind);
        Assert.Equal("jobs", d.Destination);
        Assert.Equal(body, d.Body.ToArray());
        Assert.Equal("acme", d.Headers["tenant"]);
        Assert.Equal("order.created", d.Headers["message.type"]);
        Assert.Equal(DestinationRole.Topic, d.Options.DestinationRole);
        Assert.Equal(MessagePriority.High, d.Options.Priority);
        Assert.Equal("node-a", d.ClaimOwner);
        Assert.Equal(1, d.Attempts);
        Assert.Equal("job-x", d.JobId);

        // A competing claim sees nothing while the lease is live (and d2 is not yet due).
        Assert.Empty(await store.ClaimDueDispatchesAsync(t, 100, "node-b", TimeSpan.FromMinutes(5), ct));

        // A complete from the wrong owner is ignored: after the lease lapses the dispatch is re-claimable, attempt 2.
        await store.CompleteDispatchAsync("d1", "node-b", ct);
        var reclaimed = await store.ClaimDueDispatchesAsync(t.AddMinutes(6), 100, "node-a", TimeSpan.FromMinutes(5), ct);
        Assert.Equal(2, Assert.Single(reclaimed).Attempts);

        // The owning node completes it for good.
        await store.CompleteDispatchAsync("d1", "node-a", ct);
        Assert.Empty(await store.ClaimDueDispatchesAsync(t.AddMinutes(12), 100, "node-a", TimeSpan.FromMinutes(5), ct));

        // Release reschedules a claimed dispatch to its next due time and clears ownership (recurring-occurrence path).
        var recurring = new ScheduledDispatchState
        {
            DispatchId = "d3",
            Kind = ScheduledDispatchKind.JobOccurrence,
            Destination = "cron",
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
            .Select(i => Task.Run(() => store.TryClaimAsync("claim-race", $"node-{i}", TimeSpan.FromMinutes(5), ct), ct)));
        Assert.Equal(1, claims.Count(won => won));
        var ownedBy = (await store.GetAsync("claim-race", ct))!.NodeId;
        Assert.StartsWith("node-", ownedBy);

        // Many nodes race the same Queued -> Processing transition: optimistic concurrency must admit exactly one.
        await store.CreateIfAbsentAsync(NewJob(time, "transition-race"), ct);
        var transitions = await Task.WhenAll(Enumerable.Range(0, contenders)
            .Select(i => Task.Run(() => store.TryTransitionAsync("transition-race", JobStatus.Queued, JobStatus.Processing,
                new JobStatePatch { NodeId = $"node-{i}" }, cancellationToken: ct), ct)));
        Assert.Equal(1, transitions.Count(won => won));
        Assert.Equal(JobStatus.Processing, (await store.GetAsync("transition-race", ct))!.Status);

        // A single due dispatch contested by many claimers must be handed to exactly one.
        await store.ScheduleDispatchAsync(new ScheduledDispatchState
        {
            DispatchId = "dispatch-race",
            Destination = "q",
            Body = new byte[] { 1 },
            DueUtc = time.GetUtcNow().AddMinutes(-1)
        }, ct);
        var dispatchClaims = await Task.WhenAll(Enumerable.Range(0, contenders)
            .Select(i => Task.Run(() => store.ClaimDueDispatchesAsync(time.GetUtcNow(), 100, $"node-{i}", TimeSpan.FromMinutes(5), ct), ct)));
        Assert.Equal(1, dispatchClaims.Sum(claimed => claimed.Count(d => d.DispatchId == "dispatch-race")));
    }
}
