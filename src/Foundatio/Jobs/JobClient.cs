using System;
using System.Collections.Concurrent;
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
using Microsoft.Extensions.Logging;

namespace Foundatio.Jobs;

internal sealed class JobClient : IJobClient
{
    private const int MaxCachedTypes = 1024;

    private readonly JobsRuntime _runtime;

    // Job names of argument types already checked not to be jobs, so an enqueue does not reflect over the type again.
    private readonly ConcurrentDictionary<Type, string> _argumentNames = new();
    private readonly ConcurrentDictionary<string, byte> _reportedNotRunHere = new(StringComparer.Ordinal);
    private readonly ILogger _logger;

    public JobClient(JobsRuntime runtime)
    {
        _runtime = runtime;
        _logger = runtime.LoggerFactory.CreateLogger<JobClient>();
    }

    public Task<JobHandle> EnqueueAsync<TArgs>(TArgs arguments, EnqueueOptions? options = null, CancellationToken cancellationToken = default) where TArgs : class
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return CreateAsync(GetArgumentsName(arguments), _runtime.Serializer.SerializeToBytes<object>(arguments), options, cancellationToken);
    }

    /// <summary>The job name of <paramref name="arguments"/>' runtime type, once it is known not to be a job.</summary>
    private string GetArgumentsName(object arguments)
    {
        var type = arguments.GetType();
        if (_argumentNames.TryGetValue(type, out string? name))
            return name;

        JobRegistration.ThrowIfJob(arguments, job => $"EnqueueAsync<{job}>()", nameof(arguments));
        name = JobNaming.GetName(type);
        WarnIfNotRunHere(name);
        if (_argumentNames.Count < MaxCachedTypes)
            _argumentNames.TryAdd(type, name);

        return name;
    }

    public Task<JobHandle> EnqueueAsync<TJob>(EnqueueOptions? options = null, CancellationToken cancellationToken = default) where TJob : class, IJobHandler
    {
        string name = JobNaming.GetName(typeof(TJob));
        WarnIfNotRunHere(name);
        return CreateAsync(name, ReadOnlyMemory<byte>.Empty, options, cancellationToken);
    }

    /// <summary>
    /// Warns once per job name when this process runs jobs but not this one: usually a forgotten <c>AddJob</c>. A
    /// process that runs no jobs is a producer, and its jobs are run elsewhere.
    /// </summary>
    private void WarnIfNotRunHere(string name)
    {
        if (_runtime.Jobs.Count == 0 || _runtime.Jobs.ContainsKey(name) || _reportedNotRunHere.Count >= MaxCachedTypes || !_reportedNotRunHere.TryAdd(name, 0))
            return;

        _logger.LogWarning("Enqueued job {JobName}, which no job registered in this process runs. It waits until a process that "
            + "registers it with AddJob runs it; if this process should, register the job here", name);
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

    public async Task<bool> CancelAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        if (!await _runtime.Store.RequestCancellationAsync(jobId, _runtime.Options.RetainFailedFor, cancellationToken).AnyContext())
            return false;

        // A worker elsewhere sees the request when it next renews the job's lease; this process's worker can cancel it now.
        _runtime.Worker?.RequestCancellation(jobId);
        return true;
    }

    internal async Task<JobHandle> CreateAsync(string name, ReadOnlyMemory<byte> payload, EnqueueOptions? options, CancellationToken cancellationToken, string? scheduleName = null)
    {
        if (options?.JobId is { } requestedId)
            ArgumentException.ThrowIfNullOrWhiteSpace(requestedId, nameof(options));
        if (options?.ConcurrencyKey is { } concurrencyKey)
            ArgumentException.ThrowIfNullOrWhiteSpace(concurrencyKey, nameof(options));
        if (options?.DeduplicationKey is { } deduplicationKey)
            ArgumentException.ThrowIfNullOrWhiteSpace(deduplicationKey, nameof(options));
        if (options is { Delay: not null, RunAt: not null })
            throw new ArgumentException("Set Delay or RunAt, not both.", nameof(options));

        var now = _runtime.TimeProvider.GetUtcNow();
        var runAt = options?.RunAt;
        var availableAfter = options?.Delay ?? TimeSpan.Zero;
        var availableAt = runAt ?? now.Add(availableAfter);
        var job = new JobInfo
        {
            Id = options?.JobId ?? Guid.NewGuid().ToString("N"),
            Name = name,
            Payload = payload,
            CorrelationId = options?.CorrelationId ?? MessageContext.Current?.CorrelationId ?? JobRunContext.Current?.CorrelationId ?? Activity.Current?.Id,
            ScheduleName = scheduleName,
            ConcurrencyKey = options?.ConcurrencyKey,
            DeduplicationKey = options?.DeduplicationKey,
            Headers = CreateHeaders(options),
            CreatedAt = now,
            UpdatedAt = now,
            AvailableAt = availableAt,
            ScheduledFor = runAt ?? default
        };

        // An immediate or delayed job is timed by the store's clock; this process's may be off it.
        string? id = runAt is not null
            ? await _runtime.Store.CreateAsync(job, cancellationToken).AnyContext()
            : await _runtime.Store.CreateAsync(job, availableAfter, cancellationToken).AnyContext();
        if (id is null && options?.JobId is null)
            throw new InvalidOperationException($"A job with the generated id {job.Id} already exists.");

        if (id == job.Id)
        {
            JobsInstruments.Enqueued.Add(1, new KeyValuePair<string, object?>(JobsInstruments.JobName, name));
            _runtime.SignalWorkAvailable();
        }
        else if (id is not null && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Job {JobName} with deduplication key {DeduplicationKey} is already waiting as job {JobId}", name, job.DeduplicationKey, id);
        }

        return new JobHandle(id ?? job.Id, this);
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
