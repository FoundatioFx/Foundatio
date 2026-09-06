using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Foundatio.Tests.Jobs;

public class LeaseSupervisionTests
{
    private static JobTypeRegistry CreateJobRegistry() => new(typeof(LeaseSupervisionTests).GetNestedTypes(System.Reflection.BindingFlags.NonPublic)
        .Where(t => t.IsClass && !t.IsAbstract && typeof(IJob).IsAssignableFrom(t))
        .Select(t => new JobTypeRegistration(t.FullName!, t)));

    [Fact]
    public async Task RenewalDenied_CancelsRunningJobAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new LeaseFailingStore(new InMemoryJobRuntimeStore()) { DenyRenewals = true };
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var client = new JobClient(store);
        var worker = new JobWorker(store, serviceProvider, new JobWorkerOptions { NodeId = "node-a", Lease = TimeSpan.FromSeconds(1), JobTypes = CreateJobRegistry() });

        var handle = await client.EnqueueAsync<WaitForCancellationJob>(cancellationToken: cancellationToken);
        Assert.True(await worker.RunAsync(handle.JobId, cancellationToken));

        // A clean "renewal denied" means another node owns the lease: the run must be cancelled, not left executing.
        var state = await handle.GetStateAsync(cancellationToken);
        Assert.Equal(JobStatus.Processing, state!.Status);
    }

    [Fact]
    public async Task RenewalThrowingPastLeaseWindow_CancelsRunningJobAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new LeaseFailingStore(new InMemoryJobRuntimeStore()) { ThrowOnRenewals = true };
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var client = new JobClient(store);
        var worker = new JobWorker(store, serviceProvider, new JobWorkerOptions { NodeId = "node-a", Lease = TimeSpan.FromSeconds(1), JobTypes = CreateJobRegistry() });

        var handle = await client.EnqueueAsync<WaitForCancellationJob>(cancellationToken: cancellationToken);
        Assert.True(await worker.RunAsync(handle.JobId, cancellationToken));

        // Renewal that keeps THROWING must not let the run outlive its lease: once the window passes without one
        // successful renewal, another node may have reclaimed the job, so continuing would double-run side effects.
        var state = await handle.GetStateAsync(cancellationToken);
        Assert.Equal(JobStatus.Processing, state!.Status);
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

        public Task ScheduleAsync(ScheduledJobDefinition definition, CancellationToken ct = default) => _inner.ScheduleAsync(definition, ct);
        public Task ReconcileAsync(ScheduledJobDefinition definition, CancellationToken ct = default) => _inner.ReconcileAsync(definition, ct);
        public Task<ScheduledJobDefinition?> GetScheduleAsync(string name, CancellationToken ct = default) => _inner.GetScheduleAsync(name, ct);
        public Task UnscheduleAsync(string name, CancellationToken ct = default) => _inner.UnscheduleAsync(name, ct);
        public Task<IReadOnlyList<ScheduledJobDefinition>> GetSchedulesAsync(ScheduleQuery? query = null, CancellationToken ct = default) => _inner.GetSchedulesAsync(query, ct);
        public Task<JobState?> GetAsync(string jobId, CancellationToken ct = default) => _inner.GetAsync(jobId, ct);
        public Task<int> CleanupAsync(int limit = 1000, CancellationToken ct = default) => _inner.CleanupAsync(limit, ct);
        public Task<JobPage> QueryAsync(JobQuery query, CancellationToken ct = default) => _inner.QueryAsync(query, ct);
        public Task<bool> CreateOccurrenceAsync(JobState initial, bool allowOverlap = false, CancellationToken ct = default) => _inner.CreateOccurrenceAsync(initial, allowOverlap, ct);
        public Task<JobState?> ClaimNextAsync(JobClaimRequest request, CancellationToken ct = default) => _inner.ClaimNextAsync(request, ct);
        public Task<JobState?> ClaimJobAsync(string jobId, JobClaimRequest request, CancellationToken ct = default) => _inner.ClaimJobAsync(jobId, request, ct);
        public Task<bool> CompleteJobAsync(string jobId, string claimToken, JobCompletion completion, CancellationToken ct = default) => _inner.CompleteJobAsync(jobId, claimToken, completion, ct);
        public Task<bool> RenewJobLeaseAsync(string jobId, string claimToken, TimeSpan lease, CancellationToken ct = default)
        {
            if (ThrowOnRenewals)
                throw new TimeoutException("store unreachable");
            return DenyRenewals ? Task.FromResult(false) : _inner.RenewJobLeaseAsync(jobId, claimToken, lease, ct);
        }
        public Task<bool> ReportJobProgressAsync(string jobId, string claimToken, int? percent = null, string? message = null, CancellationToken ct = default) => _inner.ReportJobProgressAsync(jobId, claimToken, percent, message, ct);
        public Task CreateIfAbsentAsync(JobState initial, CancellationToken ct = default) => _inner.CreateIfAbsentAsync(initial, ct);
        public Task<bool> RequestCancellationAsync(string jobId, CancellationToken ct = default) => _inner.RequestCancellationAsync(jobId, ct);
        public Task<bool> IsCancellationRequestedAsync(string jobId, CancellationToken ct = default) => _inner.IsCancellationRequestedAsync(jobId, ct);
        public Task ScheduleDispatchAsync(ScheduledDispatchState dispatch, CancellationToken ct = default) => _inner.ScheduleDispatchAsync(dispatch, ct);
        public Task<IReadOnlyList<ScheduledDispatchState>> ClaimDueDispatchesAsync(DateTimeOffset now, int limit, string nodeId, TimeSpan lease, CancellationToken ct = default) => _inner.ClaimDueDispatchesAsync(now, limit, nodeId, lease, ct);
        public Task CompleteDispatchAsync(string dispatchId, string nodeId, CancellationToken ct = default) => _inner.CompleteDispatchAsync(dispatchId, nodeId, ct);
        public Task ReleaseDispatchAsync(string dispatchId, string nodeId, DateTimeOffset nextDueUtc, CancellationToken ct = default) => _inner.ReleaseDispatchAsync(dispatchId, nodeId, nextDueUtc, ct);
    }
}
