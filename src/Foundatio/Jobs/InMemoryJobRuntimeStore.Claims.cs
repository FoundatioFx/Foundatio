using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Jobs;

public sealed partial class InMemoryJobRuntimeStore
{
    public Task<bool> CreateOccurrenceAsync(JobState initial, bool allowOverlap = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentException.ThrowIfNullOrWhiteSpace(initial.ScheduleName);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (_jobs.ContainsKey(initial.JobId) || (!allowOverlap && _jobs.Values.Any(s => s.ScheduleName == initial.ScheduleName
                    && s.RequiredNodeId == initial.RequiredNodeId && s.Status is JobStatus.Queued or JobStatus.Scheduled or JobStatus.Processing)))
                return Task.FromResult(false);
            EnsureCapacity();
            var now = _timeProvider.GetUtcNow();
            _jobs[initial.JobId] = initial with
            {
                CreatedUtc = initial.CreatedUtc == default ? now : initial.CreatedUtc,
                LastUpdatedUtc = now
            };
            return Task.FromResult(true);
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
            var candidates = _jobs.Values.Where(s => (jobId is null || s.JobId == jobId)
                    && (s.RequiredNodeId is null || s.RequiredNodeId == request.NodeId)
                    && s.JobType is not null && request.JobTypes.Contains(s.JobType, StringComparer.Ordinal)
                    && ((s.Status is JobStatus.Queued or JobStatus.Scheduled && (s.AvailableUtc ?? s.CreatedUtc) <= now)
                        || (s.Status == JobStatus.Processing && s.LeaseExpiresUtc <= now)))
                .OrderBy(s => s.Status == JobStatus.Processing ? s.LeaseExpiresUtc : s.AvailableUtc ?? s.CreatedUtc)
                .ThenBy(s => s.CreatedUtc).ThenBy(s => s.JobId, StringComparer.Ordinal);
            foreach (var state in candidates)
            {
                if (state.CancellationRequested || state.Attempt >= state.MaxAttempts)
                {
                    _jobs[state.JobId] = state with
                    {
                        Status = state.CancellationRequested ? JobStatus.Cancelled : JobStatus.Failed,
                        Error = state.CancellationRequested ? null : "Execution attempts exhausted after lease expiration.",
                        CompletedUtc = now,
                        LastUpdatedUtc = now,
                        NodeId = null,
                        ClaimToken = null,
                        LeaseExpiresUtc = null
                    };
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
                _jobs[state.JobId] = claimed;
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

            var kind = state.CancellationRequested ? JobCompletionKind.Cancelled : completion.Kind;
            bool retry = kind == JobCompletionKind.Failed && state.Attempt < state.MaxAttempts;
            var status = kind switch
            {
                JobCompletionKind.Succeeded => JobStatus.Completed,
                JobCompletionKind.Cancelled => JobStatus.Cancelled,
                JobCompletionKind.Interrupted => JobStatus.Queued,
                JobCompletionKind.Failed => retry ? JobStatus.Queued : JobStatus.Failed,
                _ => throw new ArgumentOutOfRangeException(nameof(completion))
            };
            _jobs[jobId] = state with
            {
                Status = status,
                Error = completion.Error,
                NodeId = null,
                ClaimToken = null,
                LeaseExpiresUtc = null,
                LastUpdatedUtc = now,
                CompletedUtc = status == JobStatus.Queued ? null : now,
                AvailableUtc = retry ? now.AddSeconds(Math.Min(300, 10 * Math.Pow(2, Math.Min(10, state.Attempt - 1)))) : now,
                Progress = status == JobStatus.Completed ? 100 : state.Progress
            };
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
            _jobs[jobId] = state with { LeaseExpiresUtc = now.Add(lease), LastUpdatedUtc = now };
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
            _jobs[jobId] = state with { Progress = percent ?? state.Progress, ProgressMessage = message ?? state.ProgressMessage, LastUpdatedUtc = now };
            return Task.FromResult(true);
        }
    }

    private bool TryGetOwnedJob(string jobId, string claimToken, DateTimeOffset now, out JobState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);
        return _jobs.TryGetValue(jobId, out state!) && state.Status == JobStatus.Processing
            && state.ClaimToken == claimToken && state.LeaseExpiresUtc > now;
    }
}
