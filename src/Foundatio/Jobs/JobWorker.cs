using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;

namespace Foundatio.Jobs;

/// <summary>Executes registered job types using atomic claims and one state machine for scheduled and ad hoc work.</summary>
public sealed class JobWorker : IJobWorker, IDisposable
{
    private readonly IJobRuntimeStore _store;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly IJobTypeRegistry _types;
    private readonly ISerializer _serializer;
    private readonly JobClaimRequest _request;
    private readonly TimeSpan _cancellationPollInterval;
    private readonly SemaphoreSlim _slots;
    private readonly int _concurrency;

    public JobWorker(IJobRuntimeStore store, IServiceProvider serviceProvider, JobWorkerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        options ??= new JobWorkerOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxConcurrency, 1);
        _store = store;
        _services = serviceProvider;
        _time = options.TimeProvider ?? TimeProvider.System;
        _types = options.JobTypes ?? serviceProvider.GetService<IJobTypeRegistry>() ?? new JobTypeRegistry();
        _serializer = options.Serializer ?? DefaultSerializer.Instance;
        _request = new JobClaimRequest
        {
            NodeId = options.NodeId ?? NodeIdentity.Current,
            JobTypes = _types.Names.ToArray(),
            Lease = options.Lease ?? TimeSpan.FromMinutes(5)
        };
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_request.Lease, TimeSpan.Zero);
        _cancellationPollInterval = options.CancellationPollInterval ?? TimeSpan.FromSeconds(1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_cancellationPollInterval, TimeSpan.Zero);
        _concurrency = options.MaxConcurrency;
        _slots = new SemaphoreSlim(_concurrency, _concurrency);
    }

    public async Task<int> RunQueuedAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (_request.JobTypes.Count == 0)
            return 0;

        int reserved = 0;
        int executed = 0;
        async Task RunSlotAsync()
        {
            while (!cancellationToken.IsCancellationRequested && Interlocked.Increment(ref reserved) <= limit)
            {
                await _slots.WaitAsync(cancellationToken).AnyContext();
                try
                {
                    var claim = await _store.ClaimNextAsync(_request, cancellationToken).AnyContext();
                    if (claim is null)
                        return;
                    await RunClaimedAsync(claim, cancellationToken).AnyContext();
                    Interlocked.Increment(ref executed);
                }
                finally
                {
                    _slots.Release();
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, Math.Min(_concurrency, limit)).Select(_ => RunSlotAsync())).AnyContext();
        return executed;
    }

    public async Task<bool> RunAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        JobClaimValidation.Validate(_request);
        await _slots.WaitAsync(cancellationToken).AnyContext();
        try
        {
            var claim = await _store.ClaimJobAsync(jobId, _request, cancellationToken).AnyContext();
            if (claim is null)
                return false;
            await RunClaimedAsync(claim, cancellationToken).AnyContext();
            return true;
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task RunClaimedAsync(JobState claim, CancellationToken stoppingToken)
    {
        var tag = new KeyValuePair<string, object?>("job", claim.Name);
        JobInstruments.Started.Add(1, tag);
        long started = _time.GetTimestamp();
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var supervision = new CancellationTokenSource();
        int leaseLost = 0;
        var leaseLoop = RenewLeaseAsync(claim, execution, () => Interlocked.Exchange(ref leaseLost, 1), supervision.Token);
        var cancellationLoop = PollCancellationAsync(claim.JobId, execution, supervision.Token);
        try
        {
            JobResult result;
            try
            {
                var context = new JobExecutionContext(claim.JobId, claim.Attempt, execution.Token, _store, claim.ClaimToken!,
                    _request.Lease, claim.Payload, claim.PayloadType, _serializer);
                execution.Token.ThrowIfCancellationRequested();
                var type = _types.Resolve(claim.JobType!);
                await using var scope = _services.CreateAsyncScope();
                var job = (IJob)ActivatorUtilities.GetServiceOrCreateInstance(scope.ServiceProvider, type);
                result = await job.TryRunAsync(context).AnyContext();
            }
            catch (OperationCanceledException) when (execution.IsCancellationRequested)
            {
                result = JobResult.Cancelled;
            }
            catch (Exception ex)
            {
                result = JobResult.FromException(ex);
            }

            if (Volatile.Read(ref leaseLost) != 0)
                return;

            var kind = stoppingToken.IsCancellationRequested ? JobCompletionKind.Interrupted
                : result.IsCancelled ? JobCompletionKind.Cancelled
                : result.IsSuccess ? JobCompletionKind.Succeeded : JobCompletionKind.Failed;
            using var settlement = new CancellationTokenSource(_request.Lease, _time);
            if (await _store.CompleteJobAsync(claim.JobId, claim.ClaimToken!, new JobCompletion { Kind = kind, Error = result.Message }, settlement.Token)
                .WaitAsync(_request.Lease, _time, settlement.Token).AnyContext())
            {
                if (kind == JobCompletionKind.Succeeded) JobInstruments.Completed.Add(1, tag);
                else if (kind == JobCompletionKind.Failed) JobInstruments.Failed.Add(1, tag);
                else if (kind == JobCompletionKind.Cancelled) JobInstruments.Cancelled.Add(1, tag);
            }
        }
        finally
        {
            await supervision.CancelAsync().AnyContext();
            await Task.WhenAll(leaseLoop, cancellationLoop).AnyContext();
            JobInstruments.RunTime.Record(_time.GetElapsedTime(started).TotalMilliseconds, tag);
        }
    }

    private async Task RenewLeaseAsync(JobState claim, CancellationTokenSource execution, Action lost, CancellationToken supervision)
    {
        var expires = claim.LeaseExpiresUtc!.Value;
        try
        {
            while (!supervision.IsCancellationRequested)
            {
                var remaining = expires - _time.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException("Execution lease expired.");
                await Task.Delay(remaining / 3, _time, supervision).AnyContext();
                var renewalStarted = _time.GetUtcNow();
                remaining = expires - renewalStarted;
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException("Execution lease expired.");
                using var deadline = new CancellationTokenSource(remaining, _time);
                using var renewal = CancellationTokenSource.CreateLinkedTokenSource(supervision, deadline.Token);
                bool renewed = await _store.RenewJobLeaseAsync(claim.JobId, claim.ClaimToken!, _request.Lease, renewal.Token)
                    .WaitAsync(remaining, _time, supervision).AnyContext();
                if (!renewed)
                    throw new JobException("Execution lease was lost.");
                expires = renewalStarted.Add(_request.Lease);
            }
        }
        catch (OperationCanceledException) when (supervision.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            lost();
            await execution.CancelAsync().AnyContext();
        }
    }

    private async Task PollCancellationAsync(string jobId, CancellationTokenSource execution, CancellationToken supervision)
    {
        while (!supervision.IsCancellationRequested)
        {
            try
            {
                if (await _store.IsCancellationRequestedAsync(jobId, supervision)
                    .WaitAsync(_cancellationPollInterval, _time, supervision).AnyContext())
                {
                    await execution.CancelAsync().AnyContext();
                    return;
                }
            }
            catch (OperationCanceledException) when (supervision.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
            }

            await _time.SafeDelay(_cancellationPollInterval, supervision).AnyContext();
        }
    }

    public void Dispose() => _slots.Dispose();
}
