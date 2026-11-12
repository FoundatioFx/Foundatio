using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Jobs.Store;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;

namespace Foundatio.Testing;

/// <summary>
/// Records what an app's messages and jobs do in an integration test: what was sent and published, how every delivery
/// ended, and which jobs ran. Enable it with <see cref="TestHarnessServiceCollectionExtensions.AddFoundatioTestHarness"/>,
/// resolve it from the service provider, act, then await an outcome and assert.
/// </summary>
/// <remarks>
/// Waits use real time, whatever <see cref="TimeProvider"/> the app uses. Retries wait for their policy's delay, so
/// give handlers and jobs short retry delays in tests (or register a FakeTimeProvider and advance it). CRON schedules
/// do not fire on their own; call <see cref="TriggerCronJobAsync{TJobOrArgs}"/>.
/// </remarks>
/// <example>
/// <code>
/// services.AddFoundatioTestHarness();   // after the app's registrations
/// await host.StartAsync();
/// var harness = host.Services.GetRequiredService&lt;FoundatioTestHarness&gt;();
///
/// await messages.PublishAsync(new OrderPlaced(1, "a@b.com"));
/// await harness.WaitForIdleAsync();
///
/// Assert.Equal(1, Assert.Single(harness.Sent&lt;SendReceipt&gt;()).OrderId);
/// Assert.Empty(harness.HandledMessages.Where(m => m.Outcome == MessageOutcome.DeadLettered));
/// </code>
/// </example>
public sealed class FoundatioTestHarness : IMessagingObserver, IJobsObserver
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMilliseconds(20);

    private readonly IServiceProvider _services;
    private readonly object _lock = new();
    private readonly List<RecordedMessage> _sent = [];
    private readonly List<RecordedMessage> _published = [];
    private readonly List<RecordedMessage> _handled = [];
    private readonly List<RecordedJob> _jobRuns = [];
    private readonly HashSet<string> _sentQueues = new(StringComparer.Ordinal);
    private TaskCompletionSource _changed = NewSignal();
    private long _version;
    private IReadOnlyList<MessageSource>? _registeredSources;

    internal FoundatioTestHarness(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>Every message sent with <c>SendAsync</c> or <c>SendBatchAsync</c>, in order.</summary>
    public IReadOnlyList<RecordedMessage> SentMessages => Snapshot(_sent);

    /// <summary>Every message published with <c>PublishAsync</c> or <c>PublishBatchAsync</c>, in order.</summary>
    public IReadOnlyList<RecordedMessage> PublishedMessages => Snapshot(_published);

    /// <summary>Every delivery to this app's handlers and its <see cref="RecordedMessage.Outcome"/>, including each retried attempt.</summary>
    public IReadOnlyList<RecordedMessage> HandledMessages => Snapshot(_handled);

    /// <summary>Every finished job run, including runs that will be retried.</summary>
    public IReadOnlyList<RecordedJob> JobRuns => Snapshot(_jobRuns);

    /// <summary>The <typeparamref name="T"/> messages sent so far, including derived types.</summary>
    public IReadOnlyList<T> Sent<T>() where T : class => SentMessages.Select(m => m.Message).OfType<T>().ToList();

    /// <summary>The <typeparamref name="T"/> messages published so far, including derived types.</summary>
    public IReadOnlyList<T> Published<T>() where T : class => PublishedMessages.Select(m => m.Message).OfType<T>().ToList();

    /// <summary>Waits until <paramref name="count"/> <typeparamref name="T"/> messages in total have been handled successfully, and returns them.</summary>
    /// <exception cref="TimeoutException">Not enough were handled within <paramref name="timeout"/> (default 30 seconds). The message says what happened instead.</exception>
    public Task<IReadOnlyList<RecordedMessage>> WaitForHandledAsync<T>(int count = 1, TimeSpan? timeout = null, CancellationToken cancellationToken = default) where T : class
    {
        return WaitForDeliveriesAsync<T>(MessageOutcome.Completed, count, timeout, cancellationToken);
    }

    /// <summary>Waits until <paramref name="count"/> <typeparamref name="T"/> messages in total have been dead-lettered, and returns them.</summary>
    /// <exception cref="TimeoutException">Not enough were dead-lettered within <paramref name="timeout"/> (default 30 seconds).</exception>
    public Task<IReadOnlyList<RecordedMessage>> WaitForDeadLetteredAsync<T>(int count = 1, TimeSpan? timeout = null, CancellationToken cancellationToken = default) where T : class
    {
        return WaitForDeliveriesAsync<T>(MessageOutcome.DeadLettered, count, timeout, cancellationToken);
    }

    /// <summary>
    /// Waits until no message is waiting for or being handled by this app's handlers, and no job is queued or running.
    /// Delayed messages and retries count as waiting. Messages sent to queues that nothing in this app consumes are ignored,
    /// but a queued job that nothing in this app runs is not: it keeps the app busy, and the timeout names it.
    /// </summary>
    /// <exception cref="TimeoutException">Still busy after <paramref name="timeout"/> (default 30 seconds). The message says what is busy.</exception>
    public async Task WaitForIdleAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var limit = timeout ?? DefaultTimeout;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(limit);
        string? busy = null;

        try
        {
            while (true)
            {
                long version = Interlocked.Read(ref _version);
                var changed = Volatile.Read(ref _changed).Task;
                busy = await FindBusyAsync(timeoutSource.Token).AnyContext();
                if (busy is null && Interlocked.Read(ref _version) == version)
                    return;

                await Task.WhenAny(changed, Task.Delay(IdlePollInterval, timeoutSource.Token)).AnyContext();
                timeoutSource.Token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Messaging and jobs were still busy after {limit}: {busy ?? "work was still being recorded"}.");
        }
    }

    /// <summary>
    /// Waits until <paramref name="count"/> runs in total of the job that takes <typeparamref name="TJobOrArgs"/> (or of
    /// the job class <typeparamref name="TJobOrArgs"/>) have finished, and returns them. Check <see cref="RecordedJob.State"/>:
    /// a run that failed for good finishes too.
    /// </summary>
    /// <exception cref="TimeoutException">Not enough runs finished within <paramref name="timeout"/> (default 30 seconds).</exception>
    public Task<IReadOnlyList<RecordedJob>> WaitForJobAsync<TJobOrArgs>(int count = 1, TimeSpan? timeout = null, CancellationToken cancellationToken = default) where TJobOrArgs : class
    {
        return WaitForJobRunsAsync(GetJobNames(typeof(TJobOrArgs)), typeof(TJobOrArgs).Name, count, timeout, cancellationToken);
    }

    /// <summary>Waits until <paramref name="count"/> runs in total of the job named <paramref name="jobName"/> have finished, and returns them.</summary>
    /// <exception cref="TimeoutException">Not enough runs finished within <paramref name="timeout"/> (default 30 seconds).</exception>
    public Task<IReadOnlyList<RecordedJob>> WaitForJobAsync(string jobName, int count = 1, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        return WaitForJobRunsAsync([jobName], jobName, count, timeout, cancellationToken);
    }

    /// <summary>
    /// Enqueues the job of the one CRON schedule that runs <typeparamref name="TJobOrArgs"/> now, as an occurrence would,
    /// regardless of its expression and overlap setting.
    /// </summary>
    /// <exception cref="InvalidOperationException">No schedule, or more than one, runs <typeparamref name="TJobOrArgs"/>.</exception>
    public Task<JobHandle> TriggerCronJobAsync<TJobOrArgs>(CancellationToken cancellationToken = default) where TJobOrArgs : class
    {
        var jobs = RequireJobs();
        var names = GetJobNames(typeof(TJobOrArgs));
        var schedules = jobs.Registrations.Schedules.Values.Where(s => names.Contains(s.JobName)).ToList();

        return schedules.Count switch
        {
            1 => TriggerAsync(jobs, schedules[0], cancellationToken),
            0 => throw new InvalidOperationException($"No CRON schedule runs {typeof(TJobOrArgs).Name}. Register one with AddCronJob."),
            _ => throw new InvalidOperationException($"Several CRON schedules run {typeof(TJobOrArgs).Name} ({String.Join(", ", schedules.Select(s => s.Name))}). Call TriggerCronJobAsync(scheduleName).")
        };
    }

    /// <summary>Enqueues the job of the CRON schedule named <paramref name="scheduleName"/> now, as an occurrence would.</summary>
    public Task<JobHandle> TriggerCronJobAsync(string scheduleName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleName);
        var jobs = RequireJobs();
        if (!jobs.Registrations.Schedules.TryGetValue(scheduleName, out var schedule))
            throw new ArgumentException($"No CRON schedule is named \"{scheduleName}\". Registered: {String.Join(", ", jobs.Registrations.Schedules.Keys)}.", nameof(scheduleName));

        return TriggerAsync(jobs, schedule, cancellationToken);
    }

    bool IJobsObserver.RunSchedules => false;

    void IMessagingObserver.OnSent(MessageDestination destination, object message, OutgoingMessage outgoing)
    {
        var record = new RecordedMessage
        {
            MessageId = outgoing.MessageId,
            MessageType = outgoing.Headers.GetValueOrDefault(KnownHeaders.MessageType) ?? String.Empty,
            Message = message,
            Address = destination.ToString(),
            CorrelationId = outgoing.Headers.GetValueOrDefault(KnownHeaders.CorrelationId),
            Headers = outgoing.Headers,
            DeliverAt = TryGetTime(outgoing.Headers, KnownHeaders.DeliverAt) ?? outgoing.DeliverAt
        };

        lock (_lock)
        {
            if (destination.Kind == MessageDestinationKind.Queue)
            {
                _sent.Add(record);
                _sentQueues.Add(destination.Name);
            }
            else
            {
                _published.Add(record);
            }
        }

        NotifyChanged();
    }

    void IMessagingObserver.OnHandled(MessageSource source, IncomingMessage message, object? payload, string outcome, string? reason, Exception? error)
    {
        string messageType = message.Headers.GetValueOrDefault(KnownHeaders.MessageType) ?? String.Empty;
        var result = outcome switch
        {
            "success" => MessageOutcome.Completed,
            "retry" => MessageOutcome.Retried,
            "dead-lettered" => MessageOutcome.DeadLettered,
            "skipped" => MessageOutcome.Skipped,
            "expired" => MessageOutcome.Expired,
            _ => MessageOutcome.Released
        };

        var record = new RecordedMessage
        {
            MessageId = message.MessageId,
            MessageType = messageType,
            Message = payload ?? TryDeserialize(messageType, message.Body),
            Address = source.ToString(),
            CorrelationId = message.Headers.GetValueOrDefault(KnownHeaders.CorrelationId),
            Attempt = Math.Max(1, message.DeliveryCount),
            Headers = message.Headers,
            Outcome = result,
            Error = DescribeError(result, outcome, reason, error),
            Exception = error
        };

        lock (_lock)
            _handled.Add(record);

        NotifyChanged();
    }

    void IJobsObserver.OnCompleted(JobInfo job, JobCompletion completion)
    {
        var record = new RecordedJob
        {
            JobId = job.Id,
            JobName = job.Name,
            Arguments = TryDeserializeArguments(job),
            State = completion.State,
            Attempt = job.Attempt,
            Message = completion.Message,
            Error = completion.Error,
            CorrelationId = job.CorrelationId,
            ScheduleName = job.ScheduleName
        };

        lock (_lock)
            _jobRuns.Add(record);

        NotifyChanged();
    }

    private MessagingRuntime? Messaging => _services.GetService<MessagingRuntime>();

    private JobsRuntime? Jobs => _services.GetService<JobsRuntime>();

    private async Task<IReadOnlyList<RecordedMessage>> WaitForDeliveriesAsync<T>(MessageOutcome outcome, int count, TimeSpan? timeout, CancellationToken cancellationToken) where T : class
    {
        return await WaitForAsync(() => HandledMessages.Where(m => m.Outcome == outcome && m.Message is T).ToList(), count, timeout, (limit, found) =>
        {
            var deliveries = HandledMessages.Where(m => m.Message is T).ToList();
            string seen = deliveries.Count == 0
                ? $"No {typeof(T).Name} was delivered to a handler ({Sent<T>().Count} sent, {Published<T>().Count} published). Is a handler registered, and has the host started?"
                : $"Deliveries of {typeof(T).Name}: {String.Join(", ", deliveries.GroupBy(m => m.Outcome).Select(g => $"{g.Count()} {g.Key}"))}."
                    + (deliveries.LastOrDefault(m => m.Error is not null) is { } failed ? $" Last error: {failed.Error}" : "");

            return $"Timed out after {limit} waiting for {count} {typeof(T).Name} message(s) to be {(outcome == MessageOutcome.Completed ? "handled" : "dead-lettered")}; {found} were. {seen}";
        }, cancellationToken).AnyContext();
    }

    private async Task<IReadOnlyList<RecordedJob>> WaitForJobRunsAsync(IReadOnlyCollection<string> names, string displayName, int count, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        return await WaitForAsync(() => JobRuns.Where(r => names.Contains(r.JobName) && r.State.IsTerminal()).ToList(), count, timeout, (limit, found) =>
        {
            var runs = JobRuns.Where(r => names.Contains(r.JobName)).ToList();
            string seen = runs.Count == 0
                ? $"No run of {displayName} has finished. Is it registered with AddJob, and has the host started?"
                : $"Runs of {displayName}: {String.Join(", ", runs.GroupBy(r => r.State).Select(g => $"{g.Count()} {g.Key}"))}."
                    + (runs.LastOrDefault(r => r.Error is not null) is { } failed ? $" Last error: {failed.Error}" : "");

            return $"Timed out after {limit} waiting for {count} run(s) of {displayName} to finish; {found} did. {seen}";
        }, cancellationToken).AnyContext();
    }

    private async Task<IReadOnlyList<TRecord>> WaitForAsync<TRecord>(Func<IReadOnlyList<TRecord>> find, int count, TimeSpan? timeout,
        Func<TimeSpan, int, string> describeTimeout, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var limit = timeout ?? DefaultTimeout;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(limit);

        while (true)
        {
            var changed = Volatile.Read(ref _changed).Task;
            var matches = find();
            if (matches.Count >= count)
                return matches;

            try
            {
                await changed.WaitAsync(timeoutSource.Token).AnyContext();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(describeTimeout(limit, find().Count));
            }
        }
    }

    /// <summary>Describes what is still busy, or returns null when everything is idle.</summary>
    /// <remarks>
    /// Message counts are read before in-flight counts, and job store counts before the worker's running count: a
    /// delivery stays in its source until it is settled, and is recorded before it stops counting as in flight.
    /// </remarks>
    private async Task<string?> FindBusyAsync(CancellationToken cancellationToken)
    {
        var busy = new List<string>();

        if (Messaging is { } messaging)
        {
            var transport = messaging.Transport as InMemoryMessageTransport
                ?? throw new InvalidOperationException("WaitForIdleAsync needs the in-memory transport. Call AddFoundatioTestHarness after AddFoundatioMessaging, so it replaces the app's transport.");

            var processors = messaging.GetProcessors();
            var sources = processors.Select(p => p.Source).Concat(GetRegisteredSources(messaging)).Distinct().ToList();
            foreach (var source in sources.Where(s => s.Kind is MessageSourceKind.Queue or MessageSourceKind.Subscription))
            {
                if (transport.GetMessageCount(source) is > 0 and var count)
                    busy.Add($"{source} has {count} message(s) waiting or in flight");
            }

            // The transport cannot count an instance subscription's messages, only report whether every source is empty.
            if (busy.Count == 0 && processors.Any(p => p.Source.Kind == MessageSourceKind.InstanceSubscription)
                && !HasUnconsumedMessages(transport, sources) && !await transport.WaitUntilIdleAsync(TimeSpan.Zero, cancellationToken).AnyContext())
            {
                busy.Add("an instance subscription has messages waiting");
            }

            foreach (var processor in processors)
            {
                if (processor.InFlight is > 0 and var inFlight)
                    busy.Add($"{processor.Source} is handling {inFlight} message(s)");
            }

            if (busy.Count > 0 && messaging.HostState != ProcessingState.Running && GetRegisteredSources(messaging).Count > 0)
                busy.Add(DescribeNotRunning("message handlers", messaging.HostState));
        }

        if (Jobs is { } jobs)
        {
            bool jobsBusy = false;
            long active = 0;
            foreach (string name in jobs.Jobs.Keys)
            {
                long queued = await jobs.Store.CountAsync(JobState.Queued, name, cancellationToken).AnyContext();
                long running = await jobs.Store.CountAsync(JobState.Running, name, cancellationToken).AnyContext();
                active += queued + running;
                if (queued + running > 0)
                {
                    jobsBusy = true;
                    busy.Add($"job {name} has {queued} queued and {running} running");
                }
            }

            long allActive = await jobs.Store.CountAsync(JobState.Queued, cancellationToken: cancellationToken).AnyContext()
                + await jobs.Store.CountAsync(JobState.Running, cancellationToken: cancellationToken).AnyContext();
            if (allActive > active)
            {
                foreach (var (name, count) in await CountUnregisteredJobsAsync(jobs, cancellationToken).AnyContext())
                    busy.Add($"job {name} has {count} queued or running, but nothing in this app runs it (is it registered with AddJob?)");
            }

            if (jobs.Worker is { Running: > 0 } worker)
            {
                jobsBusy = true;
                busy.Add($"the job worker is running {worker.Running} job(s)");
            }

            if (jobsBusy && jobs.HostState != ProcessingState.Running)
                busy.Add(DescribeNotRunning("the job worker", jobs.HostState));
        }

        return busy.Count == 0 ? null : String.Join("; ", busy);
    }

    /// <summary>Counts queued and running jobs by name, for names no job registered in this app runs.</summary>
    private static async Task<IReadOnlyList<(string Name, int Count)>> CountUnregisteredJobsAsync(JobsRuntime jobs, CancellationToken cancellationToken)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var status in new[] { JobState.Queued, JobState.Running })
        {
            string? continuation = null;
            do
            {
                var page = await jobs.Store.QueryAsync(new JobQuery { State = status, Limit = 1000, ContinuationToken = continuation }, cancellationToken).AnyContext();
                foreach (var job in page.Jobs.Where(j => !jobs.Jobs.ContainsKey(j.Name)))
                    counts[job.Name] = counts.GetValueOrDefault(job.Name) + 1;

                continuation = page.ContinuationToken;
            } while (continuation is not null);
        }

        return counts.Select(c => (c.Key, c.Value)).ToList();
    }

    private bool HasUnconsumedMessages(InMemoryMessageTransport transport, IReadOnlyCollection<MessageSource> consumed)
    {
        string[] queues;
        lock (_lock)
            queues = _sentQueues.ToArray();

        return queues.Select(MessageSource.Queue).Where(q => !consumed.Contains(q)).Any(q => transport.GetMessageCount(q) > 0);
    }

    private IReadOnlyList<MessageSource> GetRegisteredSources(MessagingRuntime messaging)
    {
        return _registeredSources ??= _services.GetService<MessagingRegistrations>()?.Handlers.Select(messaging.GetSource).Distinct().ToList() ?? [];
    }

    private JobsRuntime RequireJobs() => Jobs ?? throw new InvalidOperationException("Jobs are not configured. Call AddFoundatioJobs.");

    private Task<JobHandle> TriggerAsync(JobsRuntime jobs, CronSchedule schedule, CancellationToken cancellationToken)
    {
        var payload = schedule.Arguments is null ? ReadOnlyMemory<byte>.Empty : (ReadOnlyMemory<byte>)jobs.Serializer.SerializeToBytes<object>(schedule.Arguments);
        return _services.GetRequiredService<JobClient>().CreateAsync(schedule.JobName, payload, null, cancellationToken, schedule.Name);
    }

    private object? TryDeserialize(string messageType, ReadOnlyMemory<byte> body)
    {
        var messaging = Messaging;
        if (messaging is null || !messaging.Types.TryGetType(messageType, out var type))
            return null;

        try
        {
            return messaging.Serializer.Deserialize(body, type);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private object? TryDeserializeArguments(JobInfo job)
    {
        var jobs = Jobs;
        if (jobs is null || !jobs.Jobs.TryGetValue(job.Name, out var registration) || registration.ArgumentsType is not { } argumentsType)
            return null;

        try
        {
            return jobs.Serializer.Deserialize(job.Payload, argumentsType);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void NotifyChanged()
    {
        Interlocked.Increment(ref _version);
        Interlocked.Exchange(ref _changed, NewSignal()).TrySetResult();
    }

    private IReadOnlyList<T> Snapshot<T>(List<T> records)
    {
        lock (_lock)
            return records.ToArray();
    }

    private static string? DescribeError(MessageOutcome result, string outcome, string? reason, Exception? error) => result switch
    {
        MessageOutcome.Completed or MessageOutcome.Skipped or MessageOutcome.Expired => null,
        MessageOutcome.Released => outcome switch
        {
            "lease-lost" => "The lease was lost",
            "settle-failed" => "The message could not be settled",
            "held" => "Released behind an earlier message of its group that will be delivered again",
            _ => "Interrupted by shutdown"
        },
        _ => reason ?? (error is null ? "Retry requested" : $"{error.GetType().Name}: {error.Message}")
    };

    private static string DescribeNotRunning(string what, ProcessingState state) => state == ProcessingState.Faulted
        ? $"{what} not running: startup failed"
        : $"{what} not started (has the host started, and has the startup gate opened?)";

    private static IReadOnlyCollection<string> GetJobNames(Type type)
    {
        var names = type.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDurableJob<>))
            .Select(i => JobNaming.GetName(i.GetGenericArguments()[0]))
            .ToList();

        if (names.Count == 0 || typeof(IJob).IsAssignableFrom(type))
            names.Add(JobNaming.GetName(type));

        return names;
    }

    private static DateTimeOffset? TryGetTime(IReadOnlyDictionary<string, string> headers, string name)
    {
        return headers.TryGetValue(name, out string? value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? time : null;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
