using System;
using System.Threading;
using System.Threading.Tasks;
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
        var argumentsType = arguments.GetType();
        if (arguments is IJob)
            throw new ArgumentException($"{argumentsType.Name} is a job, not job arguments. Call EnqueueAsync<{argumentsType.Name}>() instead.", nameof(arguments));

        return CreateAsync(JobNaming.GetName(argumentsType), _runtime.Serializer.SerializeToBytes<object>(arguments), options, cancellationToken);
    }

    public Task<JobHandle> EnqueueAsync<TJob>(EnqueueOptions? options = null, CancellationToken cancellationToken = default) where TJob : IJob
    {
        return CreateAsync(JobNaming.GetName(typeof(TJob)), ReadOnlyMemory<byte>.Empty, options, cancellationToken);
    }

    public Task<JobInfo?> GetAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        return _runtime.Store.GetAsync(jobId, cancellationToken);
    }

    public Task<JobPage> QueryAsync(JobQuery? query = null, CancellationToken cancellationToken = default)
    {
        query ??= new JobQuery();
        query.Validate();
        return _runtime.Store.QueryAsync(query, cancellationToken);
    }

    public Task<long> CountAsync(JobStatus? status = null, string? name = null, CancellationToken cancellationToken = default)
    {
        return _runtime.Store.CountAsync(status, name, cancellationToken);
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
