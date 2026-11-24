using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
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
/// Messaging and jobs run on <see cref="FoundatioTestHarnessOptions.TimeProvider"/>, the system clock unless you set
/// one; waits always use real time. Retries wait for their policy's delay, so give handlers and jobs short retry delays
/// in tests, or set a fake clock and advance it. CRON schedules do not fire on their own; call
/// <see cref="TriggerCronJobAsync{TJobOrArgs}"/>. To run work one step at a time, pause processing
/// (<see cref="PauseProcessingAsync"/> or <see cref="FoundatioTestHarnessOptions.StartPaused"/>) and call
/// <see cref="ProcessNextAsync(int, TimeSpan?, CancellationToken)"/> or <see cref="ProcessPendingAsync(TimeSpan?, CancellationToken)"/>.
/// Call <see cref="Reset"/> between tests that share a host.
/// </remarks>
/// <example>
/// <code>
/// services.AddFoundatioTestHarness();   // before or after the app's registrations
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
public sealed class FoundatioTestHarness : IMessagingObserver, IJobsObserver, InMemoryMessageTransport.IReceiveGate
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMilliseconds(20);

    private readonly IServiceProvider _services;
    private readonly object _lock = new();
    private readonly List<RecordedMessage> _sent = [];
    private readonly List<RecordedMessage> _published = [];
    private readonly List<RecordedMessage> _handled = [];
    private readonly List<RecordedJob> _jobRuns = [];
    private readonly ConditionalWeakTable<MessageContext, List<MessageHandlerRegistration>> _completedHandlers = new();
    private readonly SemaphoreSlim _processing = new(1, 1);
    private TaskCompletionSource _changed = NewSignal();
    private long _version;
    private IReadOnlyList<MessageSource>? _registeredSources;
    private volatile bool _paused;
    private volatile ProcessingScope? _allowed;

    internal FoundatioTestHarness(IServiceProvider services)
    {
        _services = services;
        if (Registration?.Options.StartPaused == true)
        {
            _paused = true;
            UpdateGates(signalJobs: false);
        }
    }

    /// <summary>
    /// Whether handlers and the job worker are paused, by <see cref="PauseProcessingAsync"/> or
    /// <see cref="FoundatioTestHarnessOptions.StartPaused"/>: messages and jobs then wait until
    /// <see cref="ProcessNextAsync(int, TimeSpan?, CancellationToken)"/>, <see cref="ProcessPendingAsync(TimeSpan?, CancellationToken)"/>
    /// or <see cref="WaitForIdleAsync"/> runs them.
    /// </summary>
    public bool IsPaused => _paused;

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

    /// <summary>The deliveries <typeparamref name="THandler"/> ran to completion on, whatever the delivery's outcome.</summary>
    public IReadOnlyList<RecordedMessage> HandledBy<THandler>() where THandler : class
        => HandledMessages.Where(m => m.HandlerTypes.Contains(typeof(THandler))).ToList();

    /// <summary>
    /// Forgets everything recorded so far, so the recorded lists, counts and waits start again: call it at the start of
    /// each test when tests share a host. Work still running is recorded when it finishes, so first call
    /// <see cref="WaitForIdleAsync"/> to keep a previous test's work out of the next one.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _sent.Clear();
            _published.Clear();
            _handled.Clear();
            _jobRuns.Clear();
        }

        NotifyChanged();
    }

    /// <summary>
    /// Stops handlers and the job worker from starting new work, and waits for the work already running to finish.
    /// Messages and jobs then wait, so a test can assert what is queued, until it runs them with
    /// <see cref="ProcessPendingAsync(TimeSpan?, CancellationToken)"/> or calls <see cref="ResumeProcessing"/>.
    /// Receivers stay open, so subscriptions keep receiving published events. <see cref="IMessageAdmin"/> still reads
    /// and replays dead letters, and <c>AddLegacyMessageBus()</c> subscribers keep running; <see cref="WaitForIdleAsync"/>,
    /// <see cref="ProcessPendingAsync(TimeSpan?, CancellationToken)"/> and <see cref="ProcessNextAsync(int, TimeSpan?, CancellationToken)"/> wait for those.
    /// </summary>
    /// <exception cref="TimeoutException">Running work did not finish within <paramref name="timeout"/> (default 30 seconds).</exception>
    /// <exception cref="InvalidOperationException">The harness runs on a transport or job store shared with other hosts.</exception>
    public async Task PauseProcessingAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ThrowIfShared();
        _paused = true;
        UpdateGates(signalJobs: false);

        var limit = timeout ?? DefaultTimeout;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(limit);
        string? running = null;
        try
        {
            while ((running = FindRunning()) is not null)
                await Task.WhenAny(Volatile.Read(ref _changed).Task, Task.Delay(IdlePollInterval, timeoutSource.Token)).AnyContext();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Processing paused, but work was still running after {limit}: {running}.");
        }
    }

    /// <summary>Lets handlers and the job worker run on their own again, starting with the work that waited while paused.</summary>
    public void ResumeProcessing()
    {
        _paused = false;
        UpdateGates(signalJobs: true);
    }

    /// <summary>
    /// Runs the messages and jobs that are due now through the app's handlers and jobs, with the real dispatcher (retries,
    /// dead letters and settlement), and waits until none is left: work they send or enqueue runs too, and so do
    /// retries without a delay. Delayed messages and jobs, and retries waiting out their delay, stay pending; advance a
    /// fake <see cref="FoundatioTestHarnessOptions.TimeProvider"/>, or set
    /// <see cref="FoundatioTestHarnessOptions.RetryDelay"/>, to run them. While processing is paused it runs only this
    /// work and pauses again afterwards; the replacement for a legacy job's <c>RunUntilEmptyAsync()</c>. To run one step,
    /// like a legacy job's <c>RunAsync()</c>, call <see cref="ProcessNextAsync(int, TimeSpan?, CancellationToken)"/>.
    /// </summary>
    /// <returns>The deliveries handled and job runs finished during the call.</returns>
    /// <exception cref="TimeoutException">Work was still due or running after <paramref name="timeout"/> (default 30 seconds).</exception>
    public Task<ProcessedWork> ProcessPendingAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return ProcessAsync(ProcessingScope.All, "pending messages and jobs", timeout, cancellationToken);
    }

    /// <summary>
    /// Like <see cref="ProcessPendingAsync(TimeSpan?, CancellationToken)"/>, but runs only the queues and subscriptions
    /// that deliver <typeparamref name="TMessageOrJob"/> messages to this app's registered handlers (everything due in
    /// them), and the jobs that take <typeparamref name="TMessageOrJob"/> arguments or are that job class. Other work,
    /// including what these handlers and jobs send or enqueue, stays pending while processing is paused.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing in this app handles or runs <typeparamref name="TMessageOrJob"/>.</exception>
    public Task<ProcessedWork> ProcessPendingAsync<TMessageOrJob>(TimeSpan? timeout = null, CancellationToken cancellationToken = default) where TMessageOrJob : class
    {
        return ProcessAsync(GetScope(typeof(TMessageOrJob)), JobNaming.GetDisplayName(typeof(TMessageOrJob)), timeout, cancellationToken);
    }

    /// <summary>
    /// While processing is paused, runs the next <paramref name="count"/> deliveries and job runs that are due (fewer
    /// when fewer are due), through the real dispatcher, waits for them to finish, and stays paused. What they send,
    /// enqueue or retry waits for the next call, so a test can assert after each step: one call is a legacy job's
    /// <c>RunAsync()</c>, and a retry with a zero <see cref="FoundatioTestHarnessOptions.RetryDelay"/> is the next step.
    /// Deliveries from different queues and subscriptions run in no particular order.
    /// </summary>
    /// <returns>The deliveries handled and job runs finished during the call.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is less than 1.</exception>
    /// <exception cref="InvalidOperationException">Processing is not paused.</exception>
    /// <exception cref="TimeoutException">The work did not finish within <paramref name="timeout"/> (default 30 seconds).</exception>
    public Task<ProcessedWork> ProcessNextAsync(int count = 1, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ThrowIfNotPaused();

        // Only the app's handlers take steps, not the legacy message bus, which receives on the same transport and is never paused.
        var sources = Messaging?.GetProcessors().Select(p => p.Source).ToHashSet() ?? [];
        return ProcessAsync(new ProcessingScope(sources, null, count), "the next messages and jobs", timeout, cancellationToken);
    }

    /// <summary>
    /// Like <see cref="ProcessNextAsync(int, TimeSpan?, CancellationToken)"/>, for only the queues and subscriptions that
    /// deliver <typeparamref name="TMessageOrJob"/> messages to this app's registered handlers, and the jobs that take
    /// <typeparamref name="TMessageOrJob"/> arguments or are that job class, as in <see cref="ProcessPendingAsync{TMessageOrJob}"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is less than 1.</exception>
    /// <exception cref="InvalidOperationException">Processing is not paused, or nothing in this app handles or runs <typeparamref name="TMessageOrJob"/>.</exception>
    public Task<ProcessedWork> ProcessNextAsync<TMessageOrJob>(int count = 1, TimeSpan? timeout = null, CancellationToken cancellationToken = default) where TMessageOrJob : class
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ThrowIfNotPaused();
        return ProcessAsync(GetScope(typeof(TMessageOrJob)).WithBudget(count), JobNaming.GetDisplayName(typeof(TMessageOrJob)), timeout, cancellationToken);
    }

    /// <summary>Waits until <paramref name="count"/> <typeparamref name="T"/> messages in total have been handled successfully, and returns them.</summary>
    /// <exception cref="TimeoutException">Not enough were handled within <paramref name="timeout"/> (default 30 seconds). The message says what happened instead.</exception>
    public Task<IReadOnlyList<RecordedMessage>> WaitForHandledAsync<T>(int count = 1, TimeSpan? timeout = null, CancellationToken cancellationToken = default) where T : class
    {
        return WaitForDeliveriesAsync<T>(MessageOutcome.Completed, count, timeout, cancellationToken);
    }

    /// <summary>
    /// Waits until <typeparamref name="THandler"/> has run to completion on <paramref name="count"/> deliveries in total,
    /// and returns them. Unlike <see cref="WaitForHandledAsync{T}"/>, which counts deliveries of a message type, this
    /// tells apart handlers that share a subscription, or that handle one event on subscriptions of their own.
    /// </summary>
    /// <exception cref="TimeoutException">It did not complete enough within <paramref name="timeout"/> (default 30 seconds).</exception>
    public async Task<IReadOnlyList<RecordedMessage>> WaitForHandledByAsync<THandler>(int count = 1, TimeSpan? timeout = null, CancellationToken cancellationToken = default) where THandler : class
    {
        return await WaitForAsync(HandledBy<THandler>, count, timeout, (limit, found) =>
        {
            var registrations = _services.GetService<MessagingRegistrations>()?.Handlers.Where(h => h.HandlerType == typeof(THandler)).ToList() ?? [];
            string seen = registrations.Count == 0
                ? $"{typeof(THandler).Name} is not registered with AddConsumer or AddSubscriber."
                : $"Deliveries of {String.Join(", ", registrations.Select(r => r.MessageType.Name).Distinct())}: "
                    + String.Join(", ", HandledMessages.Where(m => registrations.Any(r => r.MessageType.IsInstanceOfType(m.Message))).GroupBy(m => m.Outcome).Select(g => $"{g.Count()} {g.Key}")) + ".";

            return $"Timed out after {limit} waiting for {typeof(THandler).Name} to handle {count} message(s); it handled {found}. {seen}";
        }, cancellationToken).AnyContext();
    }

    /// <summary>Waits until <paramref name="count"/> <typeparamref name="T"/> messages in total have been dead-lettered, and returns them.</summary>
    /// <exception cref="TimeoutException">Not enough were dead-lettered within <paramref name="timeout"/> (default 30 seconds).</exception>
    public Task<IReadOnlyList<RecordedMessage>> WaitForDeadLetteredAsync<T>(int count = 1, TimeSpan? timeout = null, CancellationToken cancellationToken = default) where T : class
    {
        return WaitForDeliveriesAsync<T>(MessageOutcome.DeadLettered, count, timeout, cancellationToken);
    }

    /// <summary>
    /// Waits until no message is waiting for or being handled by this app's handlers or its <c>AddLegacyMessageBus()</c>
    /// subscribers, and no job is queued or running. Delayed messages and retries count as waiting. Messages sent to queues that nothing in this app consumes are ignored,
    /// but a queued job that nothing in this app runs is not: it keeps the app busy, and the timeout names it.
    /// </summary>
    /// <remarks>
    /// While processing is paused, it lets all work run until the app is idle, then pauses again, so a base class shared
    /// by paused and running tests can call it either way. To run only what is due now, call
    /// <see cref="ProcessPendingAsync(TimeSpan?, CancellationToken)"/>.
    /// </remarks>
    /// <exception cref="TimeoutException">Still busy after <paramref name="timeout"/> (default 30 seconds). The message says what is busy.</exception>
    public async Task WaitForIdleAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var limit = timeout ?? DefaultTimeout;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(limit);
        string? busy = null;
        bool processing = false;

        try
        {
            if (_paused)
            {
                await _processing.WaitAsync(timeoutSource.Token).AnyContext();
                processing = true;
                _allowed = ProcessingScope.All;
                UpdateGates(signalJobs: true);
            }

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
            throw new TimeoutException($"Messaging and jobs were still busy after {limit}: {busy ?? (processing || !_paused ? "work was still being recorded" : "ProcessPendingAsync was still running")}.");
        }
        finally
        {
            if (processing)
            {
                _allowed = null;
                UpdateGates(signalJobs: false);
                _processing.Release();
            }
        }
    }

    /// <summary>
    /// Waits until <paramref name="count"/> runs in total of the job that takes <typeparamref name="TJobOrArgs"/> (or of
    /// the job class <typeparamref name="TJobOrArgs"/>: an <see cref="IJobHandler{TArgs}"/>, an <see cref="IJobHandler"/>,
    /// or a <see cref="LegacyJob{TJob}"/> or the legacy job it runs) have finished, and returns them. Check
    /// <see cref="RecordedJob.State"/>: a run that failed for good finishes too.
    /// </summary>
    /// <exception cref="TimeoutException">Not enough runs finished within <paramref name="timeout"/> (default 30 seconds).</exception>
    public Task<IReadOnlyList<RecordedJob>> WaitForJobAsync<TJobOrArgs>(int count = 1, TimeSpan? timeout = null, CancellationToken cancellationToken = default) where TJobOrArgs : class
    {
        return WaitForJobRunsAsync(GetJobNames(typeof(TJobOrArgs)), JobNaming.GetDisplayName(typeof(TJobOrArgs)), count, timeout, cancellationToken);
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
            0 => throw new InvalidOperationException($"No CRON schedule runs {JobNaming.GetDisplayName(typeof(TJobOrArgs))}. Register one with AddCronJob."),
            _ => throw new InvalidOperationException($"Several CRON schedules run {JobNaming.GetDisplayName(typeof(TJobOrArgs))} ({String.Join(", ", schedules.Select(s => s.Name))}). Call TriggerCronJobAsync(scheduleName).")
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

    void IMessagingObserver.OnHandlerCompleted(MessageContext context, MessageHandlerRegistration handler)
    {
        var handlers = _completedHandlers.GetValue(context, static _ => new List<MessageHandlerRegistration>(1));
        lock (handlers)
            handlers.Add(handler);
    }

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
                _sent.Add(record);
            else
                _published.Add(record);
        }

        NotifyChanged();
    }

    void IMessagingObserver.OnHandled(MessageSource source, IncomingMessage message, object? payload, string outcome, string? reason, Exception? error, MessageContext? context)
    {
        // Released unstarted behind an earlier message of its group, which will be delivered again: no handler ran, so
        // it is not recorded, and a ProcessNextAsync step gets back the budget it reserved for it.
        if (outcome == "held")
        {
            if (_paused && _allowed is { } allowed && allowed.Includes(source) && allowed.Refund(1))
                Registration?.Transport.NotifyReceiveGateChanged();

            NotifyChanged();
            return;
        }

        string messageType = message.Headers.GetValueOrDefault(KnownHeaders.MessageType) ?? String.Empty;
        MessageHandlerRegistration[] handlers = [];
        if (context is not null && _completedHandlers.TryGetValue(context, out var completed))
        {
            lock (completed)
                handlers = completed.ToArray();

            _completedHandlers.Remove(context);
        }

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
            Exception = error,
            Progress = context?.Progress,
            ProgressMessage = context?.ProgressMessage,
            Handlers = handlers.Select(h => h.HandlerName).ToArray(),
            HandlerTypes = handlers.Where(h => h.HandlerType is not null).Select(h => h.HandlerType!).ToArray()
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
            Message = completion.Message ?? job.Message,
            Progress = job.Progress,
            Error = completion.Error,
            CorrelationId = job.CorrelationId,
            ScheduleName = job.ScheduleName
        };

        lock (_lock)
            _jobRuns.Add(record);

        NotifyChanged();
    }

    private MessagingRuntime? Messaging => _services.GetService<MessagingRuntime>();

    private TestHarnessRegistration? Registration => _services.GetService<TestHarnessRegistration>();

    private JobsRuntime? Jobs => _services.GetService<JobsRuntime>();

    private async Task<IReadOnlyList<RecordedMessage>> WaitForDeliveriesAsync<T>(MessageOutcome outcome, int count, TimeSpan? timeout, CancellationToken cancellationToken) where T : class
    {
        return await WaitForAsync(() => HandledMessages.Where(m => m.Outcome == outcome && m.Message is T).ToList(), count, timeout, (limit, found) =>
        {
            var deliveries = HandledMessages.Where(m => m.Message is T).ToList();
            string seen = deliveries.Count == 0
                ? $"No {typeof(T).Name} was delivered to a handler ({Sent<T>().Count} sent, {Published<T>().Count} published). {DescribeMissingHandler(typeof(T))}"
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
            var transport = RequireInMemoryTransport(messaging);

            var processors = messaging.GetProcessors();
            var sources = processors.Select(p => p.Source).Concat(GetRegisteredSources(messaging)).Distinct().ToList();
            foreach (var source in sources.Where(s => s.Kind is MessageSourceKind.Queue or MessageSourceKind.Subscription))
            {
                if (transport.GetMessageCount(source) is > 0 and var count)
                    busy.Add($"{source} has {count} message(s) waiting or in flight");
            }

            foreach (var source in sources.Where(s => s.Kind == MessageSourceKind.InstanceSubscription))
            {
                var (due, delayed, inFlight) = transport.CountByState(source);
                if (due + delayed + inFlight > 0)
                    busy.Add($"{source} has {due + delayed + inFlight} message(s) waiting or in flight");
            }

            foreach (var processor in processors)
            {
                if (processor.InFlight is > 0 and var inFlight)
                    busy.Add($"{processor.Source} is handling {inFlight} message(s)");
            }

            AddLegacyBusWork(transport, busy, includeDelayed: true);

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

        if (busy.Count > 0 && _paused && _allowed is null)
            busy.Add("processing is paused: call ProcessPendingAsync() or ResumeProcessing()");

        return busy.Count == 0 ? null : String.Join("; ", busy);
    }

    private async Task<ProcessedWork> ProcessAsync(ProcessingScope scope, string what, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var limit = timeout ?? DefaultTimeout;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(limit);
        await _processing.WaitAsync(timeoutSource.Token).AnyContext();
        try
        {
            int handledBefore, runsBefore;
            lock (_lock)
            {
                handledBefore = _handled.Count;
                runsBefore = _jobRuns.Count;
            }

            _allowed = scope;
            UpdateGates(signalJobs: true);
            string? pending = null;
            try
            {
                while (true)
                {
                    long version = Interlocked.Read(ref _version);
                    var changed = Volatile.Read(ref _changed).Task;
                    pending = await FindPendingAsync(scope, timeoutSource.Token).AnyContext();
                    if (pending is null && Interlocked.Read(ref _version) == version)
                        break;

                    await Task.WhenAny(changed, Task.Delay(IdlePollInterval, timeoutSource.Token)).AnyContext();
                    timeoutSource.Token.ThrowIfCancellationRequested();
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Processing {what} did not finish within {limit}: {pending ?? "work was still being recorded"}.");
            }
            finally
            {
                _allowed = null;
                UpdateGates(signalJobs: false);
            }

            lock (_lock)
            {
                return new ProcessedWork
                {
                    HandledMessages = _handled.Skip(Math.Min(handledBefore, _handled.Count)).ToArray(),
                    JobRuns = _jobRuns.Skip(Math.Min(runsBefore, _jobRuns.Count)).ToArray()
                };
            }
        }
        finally
        {
            _processing.Release();
        }
    }

    /// <summary>Describes the work in <paramref name="scope"/> that is due now or running, or returns null when there is none.</summary>
    /// <remarks>Source counts are read before the processors' in-flight counts, and store counts before the worker's, as in <see cref="FindBusyAsync"/>.</remarks>
    private async Task<string?> FindPendingAsync(ProcessingScope scope, CancellationToken cancellationToken)
    {
        // Read before the counts: a receive reserves before it leases, and releases its reservation after.
        bool hasBudget = scope.HasBudget;
        int reserved = scope.Reserved;
        var pending = new List<string>();
        if (reserved > 0)
            pending.Add($"{reserved} message(s) are being received");

        if (Messaging is { } messaging)
        {
            if (messaging.HostState != ProcessingState.Running && GetRegisteredSources(messaging).Count > 0)
                throw new InvalidOperationException($"ProcessPendingAsync needs running handlers, but the {DescribeNotRunning("message handlers", messaging.HostState)}.");

            var transport = RequireInMemoryTransport(messaging);
            foreach (var processor in messaging.GetProcessors().Where(p => scope.Includes(p.Source)))
            {
                var (due, _, leased) = transport.CountByState(processor.Source);
                int inFlight = Math.Max(leased, processor.InFlight);
                if (due > 0 && hasBudget)
                    pending.Add($"{processor.Source} has {due} message(s) due");
                if (inFlight > 0)
                    pending.Add($"{processor.Source} is handling {inFlight} message(s)");
            }

            AddLegacyBusWork(transport, pending, includeDelayed: false);
        }

        if (Jobs is { Store: InMemoryJobStore store } jobs)
        {
            if (jobs.HostState != ProcessingState.Running && jobs.Jobs.Count > 0)
                throw new InvalidOperationException($"ProcessPendingAsync needs a running job worker, but {DescribeNotRunning("the job worker", jobs.HostState)}.");

            foreach (string name in jobs.Jobs.Keys.Where(scope.Includes))
            {
                int due = hasBudget ? store.CountDue(name) : 0;
                long running = await jobs.Store.CountAsync(JobState.Running, name, cancellationToken).AnyContext();
                if (due + running > 0)
                    pending.Add($"job {name} has {due} due and {running} running");
            }

            if (jobs.Worker is { Running: > 0 } worker)
                pending.Add($"the job worker is running {worker.Running} job(s)");
        }

        return pending.Count == 0 ? null : String.Join("; ", pending);
    }

    /// <summary>
    /// Adds what the legacy message bus's subscribers have waiting or running. They are never paused, and steps do not
    /// count them, but the waits for idle and for pending work wait for them, so a test that publishes on <see cref="IMessageBus"/> sees them run.
    /// </summary>
    private void AddLegacyBusWork(InMemoryMessageTransport transport, List<string> work, bool includeDelayed)
    {
        foreach (var processor in GetLegacyBusProcessors())
        {
            var (due, delayed, leased) = transport.CountByState(processor.Source);
            int inFlight = Math.Max(leased, processor.InFlight);
            int waiting = includeDelayed ? due + delayed : due;
            if (waiting > 0)
                work.Add($"the legacy message bus has {waiting} message(s) waiting on {processor.Source}");
            if (inFlight > 0)
                work.Add($"the legacy message bus is handling {inFlight} message(s) from {processor.Source}");
        }
    }

    /// <summary>Describes the deliveries and job runs in progress, or returns null when there are none.</summary>
    private string? FindRunning()
    {
        var running = new List<string>();
        if (Messaging is { } messaging)
        {
            foreach (var processor in messaging.GetProcessors().Where(p => p.InFlight > 0))
                running.Add($"{processor.Source} is handling {processor.InFlight} message(s)");
        }

        if (Jobs?.Worker is { Running: > 0 } worker)
            running.Add($"the job worker is running {worker.Running} job(s)");

        return running.Count == 0 ? null : String.Join("; ", running);
    }

    /// <summary>The registered handlers' sources and the registered jobs that take or are <paramref name="type"/>.</summary>
    private ProcessingScope GetScope(Type type)
    {
        var sources = new HashSet<MessageSource>();
        if (Messaging is { } messaging && _services.GetService<MessagingRegistrations>() is { } registrations)
        {
            foreach (var handler in registrations.Handlers.Where(h => h.MessageType.IsAssignableFrom(type)))
                sources.Add(messaging.GetSource(handler));
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        if (Jobs is { } jobs)
        {
            foreach (string name in GetJobNames(type).Where(jobs.Jobs.ContainsKey))
                names.Add(name);
        }

        if (sources.Count == 0 && names.Count == 0)
        {
            throw new InvalidOperationException($"Nothing in this app handles or runs {JobNaming.GetDisplayName(type)}: no handler registered with AddConsumer or AddSubscriber "
                + "takes it, and no job registered with AddJob takes it as arguments or is that job.");
        }

        return new ProcessingScope(sources, names);
    }

    /// <summary>Holds receives and claims while paused, except for the work <see cref="ProcessPendingAsync(TimeSpan?, CancellationToken)"/> is running.</summary>
    private void UpdateGates(bool signalJobs)
    {
        if (Registration is not { } registration)
            return;

        registration.Transport.SetReceiveGate(_paused ? this : null);
        registration.Store.ClaimFilter = _paused ? CanClaim : null;
        registration.Store.ClaimGate = _paused ? ReserveClaim : null;
        if (signalJobs)
            Jobs?.SignalWorkAvailable();
    }

    int InMemoryMessageTransport.IReceiveGate.Reserve(MessageSource source, int maxMessages)
    {
        if (!_paused || IsNeverPaused(source))
            return maxMessages;

        return _allowed is { } allowed && allowed.Includes(source) ? allowed.Reserve(maxMessages) : 0;
    }

    bool InMemoryMessageTransport.IReceiveGate.Release(MessageSource source, int reserved, int taken)
    {
        return _paused && !IsNeverPaused(source) && _allowed is { } allowed && allowed.Includes(source) && allowed.Release(reserved, taken);
    }

    /// <summary>
    /// Pausing holds the app's handlers, not <see cref="IMessageAdmin"/>'s reads of dead letters or the legacy message
    /// bus's subscribers (cache invalidations, for one), which run on the same transport.
    /// </summary>
    private bool IsNeverPaused(MessageSource source) => source.Kind == MessageSourceKind.DeadLetter || GetLegacyBusProcessors().Any(p => p.Source.Equals(source));

    /// <summary>The processors of <c>AddLegacyMessageBus()</c>'s subscribers, once it has any.</summary>
    private MessageProcessor[] GetLegacyBusProcessors() => Messaging?.LegacyBus?.GetProcessors() ?? [];

    private bool CanClaim(string jobName) => !_paused || _allowed?.Includes(jobName) == true;

    private bool ReserveClaim()
    {
        if (!_paused || _allowed is not { } allowed)
            return !_paused;

        if (allowed.Reserve(1) == 0)
            return false;

        allowed.Release(1, 1);
        return true;
    }

    private void ThrowIfNotPaused()
    {
        if (!_paused)
            throw new InvalidOperationException("ProcessNextAsync runs work one step at a time, so processing must be paused: set FoundatioTestHarnessOptions.StartPaused, or call PauseProcessingAsync() first.");
    }

    private void ThrowIfShared()
    {
        if (Registration?.IsShared == true)
            throw new InvalidOperationException("Pausing is not supported on a transport or job store shared with other hosts: every host that shares it would be paused.");
    }

    private string DescribeMissingHandler(Type messageType)
    {
        var handlers = _services.GetService<MessagingRegistrations>()?.Handlers.Where(h => h.MessageType.IsAssignableFrom(messageType)).ToList() ?? [];
        bool consumed = handlers.Any(h => h.Kind == MessageHandlerKind.Consumer);
        bool subscribed = handlers.Any(h => h.Kind == MessageHandlerKind.Subscriber);
        if (Sent(messageType) > 0 && subscribed && !consumed)
            return $"It was sent with SendAsync, but this app only subscribes to {messageType.Name} with AddSubscriber, and subscribers receive published messages. Did you mean PublishAsync?";
        if (Published(messageType) > 0 && consumed && !subscribed)
            return $"It was published with PublishAsync, but this app only consumes {messageType.Name} with AddConsumer, and consumers receive sent messages. Did you mean SendAsync?";
        if (_paused)
            return "Processing is paused: call ProcessPendingAsync() or ResumeProcessing().";

        return "Is a handler registered, and has the host started?";
    }

    private int Sent(Type messageType) => SentMessages.Count(m => messageType.IsInstanceOfType(m.Message));

    private int Published(Type messageType) => PublishedMessages.Count(m => messageType.IsInstanceOfType(m.Message));

    private static InMemoryMessageTransport RequireInMemoryTransport(MessagingRuntime messaging) => messaging.QueueTransport as InMemoryMessageTransport
        ?? throw new InvalidOperationException("The test harness needs its in-memory transport. Register it with AddFoundatioTestHarness().");

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

    private IReadOnlyList<MessageSource> GetRegisteredSources(MessagingRuntime messaging)
    {
        return _registeredSources ??= _services.GetService<MessagingRegistrations>()?.Handlers.Select(messaging.GetSource).Distinct().ToList() ?? [];
    }

    private JobsRuntime RequireJobs() => Jobs ?? throw new InvalidOperationException("Jobs are not configured. Call AddFoundatioJobs.");

    private Task<JobHandle> TriggerAsync(JobsRuntime jobs, CronSchedule schedule, CancellationToken cancellationToken)
    {
        var payload = schedule.Arguments is null ? ReadOnlyMemory<byte>.Empty : (ReadOnlyMemory<byte>)jobs.Serializer.SerializeToBytes<object>(schedule.Arguments);
        return _services.GetRequiredService<JobClient>().CreateAsync(schedule.JobName, payload, null, cancellationToken, schedule.Name, keys: schedule.Arguments as IHaveJobKeys);
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
            _ => "Interrupted by shutdown"
        },
        _ => reason ?? (error is null ? "Retry requested" : $"{error.GetType().Name}: {error.Message}")
    };

    private static string DescribeNotRunning(string what, ProcessingState state) => state == ProcessingState.Faulted
        ? $"{what} not running: startup failed"
        : $"{what} not started (has the host started, and has the startup gate opened?)";

    private static IReadOnlyCollection<string> GetJobNames(Type type)
    {
        var names = JobRegistration.GetArgumentsTypes(type).Select(JobNaming.GetName).ToList();
        if (names.Count == 0 || typeof(IJobHandler).IsAssignableFrom(type))
            names.Add(JobNaming.GetName(type));

        return names;
    }

    private static DateTimeOffset? TryGetTime(IReadOnlyDictionary<string, string> headers, string name)
    {
        return headers.TryGetValue(name, out string? value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? time : null;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The sources and job names a <see cref="ProcessPendingAsync(TimeSpan?, CancellationToken)"/> call runs (null for
    /// all of them), and for <see cref="ProcessNextAsync(int, TimeSpan?, CancellationToken)"/>, how many deliveries and
    /// job runs it may start.
    /// </summary>
    private sealed class ProcessingScope(IReadOnlySet<MessageSource>? sources, IReadOnlySet<string>? jobNames, int? budget = null)
    {
        private int _remaining = budget ?? 0;
        private int _reserved;

        public static ProcessingScope All { get; } = new(null, null);

        /// <summary>Whether it may start more work: always, without a budget.</summary>
        public bool HasBudget => budget is null || Volatile.Read(ref _remaining) > 0;

        /// <summary>Receives reserved and not yet released, whose messages may not show as leased yet.</summary>
        public int Reserved => Volatile.Read(ref _reserved);

        public ProcessingScope WithBudget(int count) => new(sources, jobNames, count);

        public bool Includes(MessageSource source) => sources is null || sources.Contains(source);

        public bool Includes(string jobName) => jobNames is null || jobNames.Contains(jobName);

        /// <summary>Takes up to <paramref name="max"/> from the budget, and returns how many.</summary>
        public int Reserve(int max)
        {
            if (budget is null)
                return max;

            while (true)
            {
                int remaining = Volatile.Read(ref _remaining);
                if (remaining <= 0)
                    return 0;

                int take = Math.Min(max, remaining);
                Interlocked.Add(ref _reserved, take);
                if (Interlocked.CompareExchange(ref _remaining, remaining - take, remaining) == remaining)
                    return take;

                Interlocked.Add(ref _reserved, -take);
            }
        }

        /// <summary>Gives back <paramref name="count"/> that were taken but did not run. Returns whether that made more available.</summary>
        public bool Refund(int count)
        {
            if (budget is null)
                return false;

            Interlocked.Add(ref _remaining, count);
            return true;
        }

        /// <summary>Gives back what a reservation did not use. Returns whether that made more available.</summary>
        public bool Release(int reserved, int taken)
        {
            if (budget is null)
                return false;

            if (reserved > taken)
                Interlocked.Add(ref _remaining, reserved - taken);

            Interlocked.Add(ref _reserved, -reserved);
            return reserved > taken;
        }
    }
}
