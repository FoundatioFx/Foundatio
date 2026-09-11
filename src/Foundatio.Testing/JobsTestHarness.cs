using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Jobs.Testing;

/// <summary>
/// Deterministic job tests over the real in-memory runtime without hosted workers. Tests decide when queued jobs run (<see cref="RunAllQueuedAsync"/>), when CRON
/// occurrences materialize and execute (<see cref="RunDueAsync"/> with a fixed "now"), and when a single job is
/// driven to its terminal state (<see cref="RunToCompletionAsync"/>) — no polling loop ever races the assertions.
/// <code>
/// var services = new ServiceCollection();
/// services.AddFoundatio().Jobs.UseTestHarness();
/// var harness = provider.GetRequiredService&lt;JobsTestHarness&gt;();
/// var handle = await harness.Client.EnqueueAsync&lt;SendWelcomeEmailJob&gt;();
/// await harness.RunAllQueuedAsync();
/// Assert.Equal(JobStatus.Completed, (await handle.GetStateAsync())!.Status);
/// </code>
/// </summary>
public sealed class JobsTestHarness
{
    private static readonly TimeSpan DefaultRunTimeout = TimeSpan.FromSeconds(30);

    private readonly IJobRuntimeStore _store;
    private readonly IJobWorker _worker;
    private readonly JobScheduleProcessor _processor;

    public JobsTestHarness(IJobRuntimeStore store, IJobWorker worker, JobScheduleProcessor processor, IJobClient client, IScheduledJobManager schedules)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        Client = client ?? throw new ArgumentNullException(nameof(client));
        Schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
    }

    /// <summary>The client for enqueueing the jobs under test.</summary>
    public IJobClient Client { get; }

    /// <summary>Runtime management of scheduled (CRON) jobs: add/replace definitions, enable/disable, trigger.</summary>
    public IScheduledJobManager Schedules { get; }

    /// <summary>Read access to job state for assertions.</summary>
    public IJobMonitor Monitor => _store;

    /// <summary>Runs all currently eligible queued jobs, across batches. Future delayed retries remain queued.</summary>
    public async Task<int> RunAllQueuedAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = new CancellationTokenSource(DefaultRunTimeout);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        int total = 0;
        try
        {
            while (true)
            {
                operation.Token.ThrowIfCancellationRequested();
                int executed = await _worker.RunQueuedAsync(cancellationToken: operation.Token).WaitAsync(operation.Token).ConfigureAwait(false);
                total += executed;
                if (executed < 100)
                    return total;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException("Queued jobs did not become idle within 30 seconds. Check for a blocked job or work that continually enqueues more jobs.");
        }
    }

    /// <summary>
    /// Materializes CRON occurrences due at the supplied time and runs all currently eligible queued jobs.
    /// Scheduled messages are drained separately through ScheduledMessageDispatcher.
    /// </summary>
    public async Task<int> RunDueAsync(DateTimeOffset? now = null, CancellationToken cancellationToken = default)
    {
        var utcNow = now ?? DateTimeOffset.UtcNow;
        await _processor.EnqueueDueOccurrencesAsync(utcNow, cancellationToken).ConfigureAwait(false);
        return await RunAllQueuedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs only the handle's job until it reaches a terminal state (Completed, Failed, Cancelled, or
    /// DeadLettered) and returns that state. Throws <see cref="TimeoutException"/> naming the job's current status
    /// when it is still non-terminal after 30s.
    /// </summary>
    public async Task<JobState> RunToCompletionAsync(JobHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        long deadline = Environment.TickCount64 + (long)DefaultRunTimeout.TotalMilliseconds;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _worker.RunAsync(handle.JobId, cancellationToken).ConfigureAwait(false);

            var state = await handle.GetStateAsync(cancellationToken).ConfigureAwait(false);
            if (state is { Status: JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.DeadLettered })
                return state;

            if (Environment.TickCount64 >= deadline)
                throw new TimeoutException($"Job \"{handle.JobId}\" did not reach a terminal state in time; current status: {state?.Status.ToString() ?? "not found"}.");

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }
}
