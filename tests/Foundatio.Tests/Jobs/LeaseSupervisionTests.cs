using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Foundatio.Tests.Jobs;

public class LeaseSupervisionTests
{
    [Fact]
    public async Task RenewalDenied_CancelsRunningJobAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new LeaseFailingStore(new InMemoryJobRuntimeStore()) { DenyRenewals = true };
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var client = new JobClient(store);
        var worker = new JobWorker(store, serviceProvider, nodeId: "node-a", lease: TimeSpan.FromSeconds(1));

        var handle = await client.EnqueueAsync<WaitForCancellationJob>(cancellationToken: cancellationToken);
        Assert.True(await worker.RunAsync(handle.JobId, cancellationToken));

        // A clean "renewal denied" means another node owns the lease: the run must be cancelled, not left executing.
        var state = await handle.GetStateAsync(cancellationToken);
        Assert.Equal(JobStatus.Cancelled, state!.Status);
    }

    [Fact]
    public async Task RenewalThrowingPastLeaseWindow_CancelsRunningJobAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new LeaseFailingStore(new InMemoryJobRuntimeStore()) { ThrowOnRenewals = true };
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var client = new JobClient(store);
        var worker = new JobWorker(store, serviceProvider, nodeId: "node-a", lease: TimeSpan.FromSeconds(1));

        var handle = await client.EnqueueAsync<WaitForCancellationJob>(cancellationToken: cancellationToken);
        Assert.True(await worker.RunAsync(handle.JobId, cancellationToken));

        // Renewal that keeps THROWING must not let the run outlive its lease: once the window passes without one
        // successful renewal, another node may have reclaimed the job, so continuing would double-run side effects.
        var state = await handle.GetStateAsync(cancellationToken);
        Assert.Equal(JobStatus.Cancelled, state!.Status);
    }

    private sealed class WaitForCancellationJob : IJob
    {
        public async Task<JobResult> RunAsync(JobExecutionContext context)
        {
            try
            {
                // Runs "forever" unless the supervision loop cancels the run.
                await Task.Delay(TimeSpan.FromSeconds(30), context.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                return JobResult.CancelledWithMessage("lease lost");
            }

            return JobResult.FailedWithMessage("was never cancelled");
        }
    }

    // Delegates everything to the inner store; renewals can be denied (clean lease loss) or made to throw (store outage).
    private sealed class LeaseFailingStore : IJobRuntimeStore
    {
        private readonly IJobRuntimeStore _inner;

        public LeaseFailingStore(IJobRuntimeStore inner) => _inner = inner;

        public bool DenyRenewals { get; set; }
        public bool ThrowOnRenewals { get; set; }

        public Task<bool> RenewClaimAsync(string jobId, string nodeId, TimeSpan lease, CancellationToken cancellationToken = default)
        {
            if (ThrowOnRenewals)
                throw new TimeoutException("store unreachable");

            return DenyRenewals ? Task.FromResult(false) : _inner.RenewClaimAsync(jobId, nodeId, lease, cancellationToken);
        }

        public Task<JobState?> GetAsync(string jobId, CancellationToken ct = default) => _inner.GetAsync(jobId, ct);
        public Task<IReadOnlyList<JobState>> QueryAsync(JobQuery query, CancellationToken ct = default) => _inner.QueryAsync(query, ct);
        public Task CreateIfAbsentAsync(JobState initial, CancellationToken ct = default) => _inner.CreateIfAbsentAsync(initial, ct);
        public Task<bool> TryTransitionAsync(string jobId, JobStatus expectedStatus, JobStatus newStatus, JobStatePatch? patch = null, string? expectedNodeId = null, CancellationToken ct = default) => _inner.TryTransitionAsync(jobId, expectedStatus, newStatus, patch, expectedNodeId, ct);
        public Task<bool> TryClaimAsync(string jobId, string nodeId, TimeSpan lease, CancellationToken ct = default) => _inner.TryClaimAsync(jobId, nodeId, lease, ct);
        public Task<bool> ReleaseClaimAsync(string jobId, string nodeId, CancellationToken ct = default) => _inner.ReleaseClaimAsync(jobId, nodeId, ct);
        public Task<IReadOnlyList<JobState>> GetExpiredProcessingAsync(DateTimeOffset now, int limit, CancellationToken ct = default) => _inner.GetExpiredProcessingAsync(now, limit, ct);
        public Task<bool> TryReclaimExpiredAsync(string jobId, DateTimeOffset now, string expectedNodeId, JobStatus newStatus, JobStatePatch? patch = null, CancellationToken ct = default) => _inner.TryReclaimExpiredAsync(jobId, now, expectedNodeId, newStatus, patch, ct);
        public Task SetProgressAsync(string jobId, int? percent = null, string? message = null, CancellationToken ct = default) => _inner.SetProgressAsync(jobId, percent, message, ct);
        public Task IncrementAttemptAsync(string jobId, CancellationToken ct = default) => _inner.IncrementAttemptAsync(jobId, ct);
        public Task<bool> RequestCancellationAsync(string jobId, CancellationToken ct = default) => _inner.RequestCancellationAsync(jobId, ct);
        public Task<bool> IsCancellationRequestedAsync(string jobId, CancellationToken ct = default) => _inner.IsCancellationRequestedAsync(jobId, ct);
        public Task ScheduleDispatchAsync(ScheduledDispatchState dispatch, CancellationToken ct = default) => _inner.ScheduleDispatchAsync(dispatch, ct);
        public Task<IReadOnlyList<ScheduledDispatchState>> ClaimDueDispatchesAsync(DateTimeOffset now, int limit, string nodeId, TimeSpan lease, CancellationToken ct = default) => _inner.ClaimDueDispatchesAsync(now, limit, nodeId, lease, ct);
        public Task CompleteDispatchAsync(string dispatchId, string nodeId, CancellationToken ct = default) => _inner.CompleteDispatchAsync(dispatchId, nodeId, ct);
        public Task ReleaseDispatchAsync(string dispatchId, string nodeId, DateTimeOffset nextDueUtc, CancellationToken ct = default) => _inner.ReleaseDispatchAsync(dispatchId, nodeId, nextDueUtc, ct);
    }
}
