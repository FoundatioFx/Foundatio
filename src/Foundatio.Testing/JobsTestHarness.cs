using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Jobs.Testing;

/// <summary>
/// Deterministic job tests without the runtime pump: the harness wraps the real in-memory job runtime with the auto
/// pump disabled, so the test decides exactly when queued jobs run (<see cref="RunAllQueuedAsync"/>), when CRON
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

    /// <summary>Runs every currently-queued job to a settled state in this call. Returns the number completed.</summary>
    public Task<int> RunAllQueuedAsync(CancellationToken cancellationToken = default)
    {
        return _worker.RunQueuedAsync(cancellationToken: cancellationToken);
    }

    /// <summary>
    /// One deterministic scheduler tick: materializes every CRON occurrence due at <paramref name="now"/> (real now
    /// when null), then claims and executes the due dispatches — occurrences run and delayed messages materialize in
    /// this call, exactly as one pump pass would. Returns the number of dispatches (occurrences plus scheduled
    /// messages) completed.
    /// </summary>
    public async Task<int> RunDueAsync(DateTimeOffset? now = null, CancellationToken cancellationToken = default)
    {
        var utcNow = now ?? DateTimeOffset.UtcNow;
        await _processor.EnqueueDueOccurrencesAsync(utcNow, cancellationToken).ConfigureAwait(false);
        return await _processor.RunDueOccurrencesAsync(utcNow, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs worker passes until the handle's job reaches a terminal state (Completed, Failed, Cancelled, or
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
            await _worker.RunQueuedAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            var state = await handle.GetStateAsync(cancellationToken).ConfigureAwait(false);
            if (state is { Status: JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.DeadLettered })
                return state;

            if (Environment.TickCount64 >= deadline)
                throw new TimeoutException($"Job \"{handle.JobId}\" did not reach a terminal state in time; current status: {state?.Status.ToString() ?? "not found"}.");

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }
}
