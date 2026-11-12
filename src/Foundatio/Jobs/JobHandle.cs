using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Jobs;

/// <summary>A reference to an enqueued job.</summary>
public sealed class JobHandle
{
    private static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(1);

    private readonly IJobClient _client;

    internal JobHandle(string id, IJobClient client)
    {
        Id = id;
        _client = client;
    }

    public string Id { get; }

    public Task<JobInfo?> GetAsync(CancellationToken cancellationToken = default) => _client.GetAsync(Id, cancellationToken);

    public Task<bool> CancelAsync(CancellationToken cancellationToken = default) => _client.CancelAsync(Id, cancellationToken);

    /// <summary>Waits until the job succeeds, fails or is cancelled.</summary>
    /// <param name="timeout">How long to wait, in real time whatever <see cref="TimeProvider"/> the app uses. Defaults to five minutes.</param>
    /// <param name="cancellationToken">Stops waiting; the job keeps running.</param>
    /// <exception cref="TimeoutException">The job did not finish within <paramref name="timeout"/>.</exception>
    public async Task<JobInfo> WaitForCompletionAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var limit = timeout ?? TimeSpan.FromMinutes(5);
        long started = Stopwatch.GetTimestamp();
        var interval = TimeSpan.FromMilliseconds(25);

        while (true)
        {
            var job = await GetAsync(cancellationToken).AnyContext() ?? throw new InvalidOperationException($"Job {Id} no longer exists.");
            if (job.Status.IsTerminal())
                return job;

            var remaining = limit - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"Job {Id} is still {job.Status} after {limit}.");

            await Task.Delay(interval < remaining ? interval : remaining, cancellationToken).AnyContext();
            interval = TimeSpan.FromTicks(Math.Min(interval.Ticks * 2, MaxPollInterval.Ticks));
        }
    }

    public override string ToString() => Id;
}
