using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs.Store;
using Foundatio.Messaging;
using Foundatio.Serializer;
using Foundatio.Utility;

namespace Foundatio.Jobs;

internal sealed class JobClient : IJobClient
{
    private readonly JobsRuntime _runtime;

    public JobClient(JobsRuntime runtime)
    {
        _runtime = runtime;
    }

    public Task<JobHandle> EnqueueAsync<TArgs>(TArgs arguments, EnqueueOptions? options = null, CancellationToken cancellationToken = default) where TArgs : class
    {
        ArgumentNullException.ThrowIfNull(arguments);
        JobRegistration.ThrowIfJob(arguments, job => $"EnqueueAsync<{job}>()", nameof(arguments));
        return CreateAsync(JobNaming.GetName(arguments.GetType()), _runtime.Serializer.SerializeToBytes<object>(arguments), options, cancellationToken);
    }

    public Task<JobHandle> EnqueueAsync<TJob>(EnqueueOptions? options = null, CancellationToken cancellationToken = default) where TJob : class, IDurableJob
    {
        return CreateAsync(JobNaming.GetName(typeof(TJob)), ReadOnlyMemory<byte>.Empty, options, cancellationToken);
    }

    public async Task<JobDetails?> GetAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        return await _runtime.Store.GetAsync(jobId, cancellationToken).AnyContext() is { } job ? new JobDetails(job, _runtime.Serializer) : null;
    }

    public async Task<JobPage<JobDetails>> QueryAsync(JobQuery? query = null, CancellationToken cancellationToken = default)
    {
        query ??= new JobQuery();
        query.Validate();
        var page = await _runtime.Store.QueryAsync(query, cancellationToken).AnyContext();
        return new JobPage<JobDetails>(page.Jobs.Select(job => new JobDetails(job, _runtime.Serializer)).ToList(), page.ContinuationToken);
    }

    public Task<long> CountAsync(JobState? state = null, string? name = null, CancellationToken cancellationToken = default)
    {
        return _runtime.Store.CountAsync(state, name, cancellationToken);
    }

    public Task<bool> CancelAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        return _runtime.Store.RequestCancellationAsync(jobId, _runtime.Options.RetainFailedFor, cancellationToken);
    }

    internal async Task<JobHandle> CreateAsync(string name, ReadOnlyMemory<byte> payload, EnqueueOptions? options, CancellationToken cancellationToken, string? scheduleName = null)
    {
        if (options?.JobId is { } requestedId)
            ArgumentException.ThrowIfNullOrWhiteSpace(requestedId, nameof(options));

        var now = _runtime.TimeProvider.GetUtcNow();
        var job = new JobInfo
        {
            Id = options?.JobId ?? Guid.NewGuid().ToString("N"),
            Name = name,
            Payload = payload,
            CorrelationId = options?.CorrelationId ?? MessageContext.Current?.CorrelationId ?? JobRunContext.Current?.CorrelationId,
            ScheduleName = scheduleName,
            CreatedAt = now,
            UpdatedAt = now,
            AvailableAt = options?.RunAt ?? (options?.Delay is { } delay ? now.Add(delay) : now)
        };

        bool created = await _runtime.Store.CreateAsync(job, cancellationToken).AnyContext();
        if (!created && options?.JobId is null)
            throw new InvalidOperationException($"A job with the generated id {job.Id} already exists.");

        if (created)
        {
            JobsInstruments.Enqueued.Add(1, new System.Collections.Generic.KeyValuePair<string, object?>("job.name", name));
            _runtime.SignalWorkAvailable();
        }

        return new JobHandle(job.Id, this);
    }
}
