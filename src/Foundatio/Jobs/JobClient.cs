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

internal sealed class JobClient : IJobClient, IDisposable
{
    private const int MaxCachedTypes = 1024;

    private readonly JobsRuntime _runtime;

    // Job names of argument types already checked not to be jobs, and of job types, so an enqueue does not reflect over the type again.
    private readonly ConcurrentDictionary<Type, EnqueuedJobName> _argumentNames = new();
    private readonly ConcurrentDictionary<Type, EnqueuedJobName> _jobNames = new();
    private readonly ConcurrentDictionary<string, byte> _reportedNotRunHere = new(StringComparer.Ordinal);
    private readonly UnrunJobWatch? _unrunJobs;
    private readonly ILogger _logger;

    public JobClient(JobsRuntime runtime)
    {
        _runtime = runtime;
        _logger = runtime.LoggerFactory.CreateLogger<JobClient>();

        // The test harness reports a queued job nothing in the app runs when a test waits for the app to be idle.
        _unrunJobs = runtime.Observer is null ? new UnrunJobWatch(runtime, _logger) : null;
    }

    public Task<JobHandle> EnqueueAsync<TArgs>(TArgs arguments, EnqueueOptions? options = null, CancellationToken cancellationToken = default) where TArgs : class
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var name = GetArgumentsName(arguments);
        return CreateAsync(name.Name, _runtime.Serializer.SerializeToBytes<object>(arguments), options, cancellationToken, keys: arguments as IHaveJobKeys, enqueuedName: name);
    }

    /// <summary>The job name of <paramref name="arguments"/>' runtime type, once it is known not to be a job.</summary>
    private EnqueuedJobName GetArgumentsName(object arguments)
    {
        var type = arguments.GetType();
        if (_argumentNames.TryGetValue(type, out var name))
            return name;

        JobRegistration.ThrowIfJob(arguments, job => $"EnqueueAsync<{job}>()", nameof(arguments));
        return AddName(_argumentNames, type);
    }

    public Task<JobHandle> EnqueueAsync<TJob>(EnqueueOptions? options = null, CancellationToken cancellationToken = default) where TJob : class, IJobHandler
    {
        var name = _jobNames.TryGetValue(typeof(TJob), out var cached) ? cached : AddName(_jobNames, typeof(TJob));
        return CreateAsync(name.Name, ReadOnlyMemory<byte>.Empty, options, cancellationToken, enqueuedName: name);
    }

    private EnqueuedJobName AddName(ConcurrentDictionary<Type, EnqueuedJobName> names, Type type)
    {
        string jobName = JobNaming.GetName(type);
        WarnIfNotRunHere(jobName);

        // Past the cache's limit a name is not followed, since each enqueue would create a new one to follow.
        var name = new EnqueuedJobName(jobName, runsHere: _runtime.Jobs.ContainsKey(jobName) || names.Count >= MaxCachedTypes);
        return names.Count < MaxCachedTypes ? names.GetOrAdd(type, name) : name;
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

    internal async Task<JobHandle> CreateAsync(string name, ReadOnlyMemory<byte> payload, EnqueueOptions? options, CancellationToken cancellationToken,
        string? scheduleName = null, DateTimeOffset? retainUntil = null, IHaveJobKeys? keys = null, EnqueuedJobName? enqueuedName = null)
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
            ConcurrencyKey = options?.ConcurrencyKey ?? ValidateKey(keys, keys?.ConcurrencyKey, nameof(IHaveJobKeys.ConcurrencyKey)),
            DeduplicationKey = options?.DeduplicationKey ?? ValidateKey(keys, keys?.DeduplicationKey, nameof(IHaveJobKeys.DeduplicationKey)),
            Headers = CreateHeaders(options),
            CreatedAt = now,
            UpdatedAt = now,
            AvailableAt = availableAt,
            ScheduledFor = runAt ?? default,
            RetainUntil = retainUntil
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
            if (enqueuedName is { NeedsCheck: true })
                _unrunJobs?.OnCreated(enqueuedName, job);
        }
        else if (id is not null && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Job {JobName} with deduplication key {DeduplicationKey} is already waiting as job {JobId}", name, job.DeduplicationKey, id);
        }

        return new JobHandle(id ?? job.Id, this);
    }

    public void Dispose() => _unrunJobs?.Dispose();

    /// <summary>Returns a key the arguments chose, or throws when it is blank.</summary>
    internal static string? ValidateKey(IHaveJobKeys? keys, string? key, string member)
    {
        if (key is not null && String.IsNullOrWhiteSpace(key))
            throw new ArgumentException($"{keys!.GetType().Name}'s IHaveJobKeys.{member} is blank. Return null for no key.", "arguments");

        return key;
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
