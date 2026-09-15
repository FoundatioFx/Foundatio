using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Jobs;

public sealed partial class InMemoryJobRuntimeStore
{
    public Task<JobOccurrenceResult> CreateOccurrenceAsync(JobState initial, bool allowOverlap = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentException.ThrowIfNullOrWhiteSpace(initial.ScheduleName);
        if (initial.ExecutionOwner != JobExecutionOwner.Runtime) throw new ArgumentException("Scheduled occurrences must be owned by the job runtime.", nameof(initial));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            ValidatePayload(initial.Payload?.Length ?? 0);
            initial.RetryPolicy.Validate();
            PurgeDeduplication();
            if (_jobs.ContainsKey(initial.JobId) || _deduplication.ContainsKey(initial.JobId))
                return Task.FromResult(JobOccurrenceResult.AlreadyExists);
            if (!allowOverlap && _active.Values.Any(s => s.ScheduleName == initial.ScheduleName
                    && s.RequiredNodeId == initial.RequiredNodeId && s.Status is JobStatus.Queued or JobStatus.Scheduled or JobStatus.Processing))
                return Task.FromResult(JobOccurrenceResult.OverlapBlocked);
            EnsureCapacity();
            var now = _timeProvider.GetUtcNow();
            StoreJob(initial with
            {
                CreatedUtc = initial.CreatedUtc == default ? now : initial.CreatedUtc,
                LastUpdatedUtc = now
            });
            return Task.FromResult(JobOccurrenceResult.Created);
        }
    }

    public Task<JobState?> ClaimNextAsync(JobClaimRequest request, CancellationToken cancellationToken = default)
        => ClaimAsync(null, request, cancellationToken);

    public Task<JobState?> ClaimJobAsync(string jobId, JobClaimRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        return ClaimAsync(jobId, request, cancellationToken);
    }

    private Task<JobState?> ClaimAsync(string? jobId, JobClaimRequest request, CancellationToken cancellationToken)
    {
        JobClaimValidation.Validate(request);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var now = _timeProvider.GetUtcNow();
            if (_active.Count == 0)
                return Task.FromResult<JobState?>(null);
            var candidates = _active.Values.Where(s => s.ExecutionOwner == JobExecutionOwner.Runtime && (jobId is null || s.JobId == jobId)
                    && (s.RequiredNodeId is null || s.RequiredNodeId == request.NodeId)
                    && s.JobType is not null && request.JobTypes.Contains(s.JobType, StringComparer.Ordinal)
                    && ((s.Status is JobStatus.Queued or JobStatus.Scheduled && (s.AvailableUtc ?? s.CreatedUtc) <= now)
                        || (s.Status == JobStatus.Processing && s.LeaseExpiresUtc <= now)))
                .OrderBy(s => s.Status == JobStatus.Processing ? s.LeaseExpiresUtc : s.AvailableUtc ?? s.CreatedUtc)
                .ThenBy(s => s.CreatedUtc).ThenBy(s => s.JobId, StringComparer.Ordinal);
            foreach (var state in candidates)
            {
                bool expired = state.Attempt == 0 && state.RequiredNodeId is not null && state.ExpiresUtc <= now;
                if (expired || state.CancellationRequested || state.Attempt >= state.MaxAttempts)
                {
                    StoreJob(state with
                    {
                        Status = expired || state.CancellationRequested ? JobStatus.Cancelled : JobStatus.Failed,
                        Error = expired || state.CancellationRequested ? null : "Execution attempts exhausted after lease expiration.",
                        ResultMessage = expired ? "Unclaimed per-node occurrence expired." : null,
                        CompletedUtc = now,
                        LastUpdatedUtc = now,
                        NodeId = null,
                        ClaimToken = null,
                        LeaseExpiresUtc = null
                    });
                    continue;
                }

                var claimed = state with
                {
                    Status = JobStatus.Processing,
                    NodeId = request.NodeId,
                    ClaimToken = Guid.NewGuid().ToString("N"),
                    LeaseExpiresUtc = now.Add(request.Lease),
                    StartedUtc = now,
                    CompletedUtc = null,
                    LastUpdatedUtc = now,
                    Attempt = state.Attempt + 1
                };
                StoreJob(claimed);
                return Task.FromResult<JobState?>(claimed);
            }

            return Task.FromResult<JobState?>(null);
        }
    }

    public Task<bool> CompleteJobAsync(string jobId, string claimToken, JobCompletion completion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var now = _timeProvider.GetUtcNow();
            if (!TryGetOwnedJob(jobId, claimToken, now, out var state))
                return Task.FromResult(false);

            bool broker = state.ExecutionOwner == JobExecutionOwner.Broker;
            var kind = !broker && state.CancellationRequested ? JobCompletionKind.Cancelled : completion.Kind;
            bool retry = kind == JobCompletionKind.Failed && completion.Retryable && (broker || state.Attempt < state.MaxAttempts);
            var status = kind switch
            {
                JobCompletionKind.Succeeded => JobStatus.Completed,
                JobCompletionKind.Cancelled => JobStatus.Cancelled,
                JobCompletionKind.Interrupted => broker ? JobStatus.RetryPending : JobStatus.Queued,
                JobCompletionKind.Failed => retry ? (broker ? JobStatus.RetryPending : JobStatus.Queued) : JobStatus.Failed,
                _ => throw new ArgumentOutOfRangeException(nameof(completion))
            };
            StoreJob(state with
            {
                Status = status,
                Error = kind == JobCompletionKind.Failed ? completion.Error : null,
                ResultMessage = completion.Message,
                NodeId = broker ? state.NodeId : null,
                ClaimToken = null,
                LeaseExpiresUtc = null,
                LastUpdatedUtc = now,
                CompletedUtc = status is JobStatus.Queued or JobStatus.RetryPending ? null : now,
                AvailableUtc = broker ? null : retry ? now.Add(state.RetryPolicy.GetDelay(state.Attempt)) : now,
                Progress = status == JobStatus.Completed ? 100 : state.Progress
            });
            return Task.FromResult(true);
        }
    }

    public Task<bool> RenewJobLeaseAsync(string jobId, string claimToken, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var now = _timeProvider.GetUtcNow();
            if (!TryGetOwnedJob(jobId, claimToken, now, out var state))
                return Task.FromResult(false);
            if (state.ExecutionOwner == JobExecutionOwner.Broker) return Task.FromResult(false);
            StoreJob(state with { LeaseExpiresUtc = now.Add(lease), LastUpdatedUtc = now });
            return Task.FromResult(true);
        }
    }

    public Task<bool> ReportJobProgressAsync(string jobId, string claimToken, int? percent = null, string? message = null, CancellationToken cancellationToken = default)
    {
        if (percent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(percent));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var now = _timeProvider.GetUtcNow();
            if (!TryGetOwnedJob(jobId, claimToken, now, out var state))
                return Task.FromResult(false);
            StoreJob(state with { Progress = percent ?? state.Progress, ProgressMessage = message ?? state.ProgressMessage, LastHeartbeatUtc = now, LastUpdatedUtc = now });
            return Task.FromResult(true);
        }
    }

    private bool TryGetOwnedJob(string jobId, string claimToken, DateTimeOffset now, out JobState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);
        PurgeBrokerHistory();
        return _jobs.TryGetValue(jobId, out state!) && state.Status == JobStatus.Processing
            && state.ClaimToken == claimToken && (state.ExecutionOwner == JobExecutionOwner.Broker || state.LeaseExpiresUtc > now);
    }
}
