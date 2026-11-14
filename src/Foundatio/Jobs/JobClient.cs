using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
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

    public Task<JobHandle> EnqueueAsync<TJob>(EnqueueOptions? options = null, CancellationToken cancellationToken = default) where TJob : class, IJobHandler
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
            CorrelationId = options?.CorrelationId ?? MessageContext.Current?.CorrelationId ?? JobRunContext.Current?.CorrelationId ?? Activity.Current?.Id,
            ScheduleName = scheduleName,
            Headers = CreateHeaders(options),
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

    /// <summary>The caller's headers, plus the current activity's W3C trace context unless the caller set a <c>traceparent</c>.</summary>
    private static IReadOnlyDictionary<string, string> CreateHeaders(EnqueueOptions? options)
    {
        Dictionary<string, string>? headers = null;
        if (options?.Headers is { Count: > 0 } requested)
        {
            headers = new Dictionary<string, string>(requested.Count + 2, StringComparer.Ordinal);
            foreach (var (name, value) in requested)
            {
                if (String.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("Header names cannot be empty.", nameof(options));
                if (value is null)
                    throw new ArgumentException($"Header {name} has no value.", nameof(options));

                headers[name] = value;
            }
        }

        if (Activity.Current is { IdFormat: ActivityIdFormat.W3C, Id: { } traceParent } activity && headers?.ContainsKey(KnownHeaders.TraceParent) != true)
        {
            headers ??= new Dictionary<string, string>(2, StringComparer.Ordinal);
            headers[KnownHeaders.TraceParent] = traceParent;
            if (!String.IsNullOrEmpty(activity.TraceStateString))
                headers.TryAdd(KnownHeaders.TraceState, activity.TraceStateString);
        }

        return headers ?? (IReadOnlyDictionary<string, string>)ReadOnlyDictionary<string, string>.Empty;
    }
}
