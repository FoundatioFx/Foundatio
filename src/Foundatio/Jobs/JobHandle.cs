using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;

namespace Foundatio.Jobs;

/// <summary>A reference to an enqueued job.</summary>
public sealed class JobHandle
{
    private readonly IJobClient _client;
    private readonly TimeProvider _timeProvider;

    internal JobHandle(string id, IJobClient client, TimeProvider timeProvider)
    {
        Id = id;
        _client = client;
        _timeProvider = timeProvider;
    }

    public string Id { get; }

    public Task<JobInfo?> GetAsync(CancellationToken cancellationToken = default) => _client.GetAsync(Id, cancellationToken);

    public Task<bool> CancelAsync(CancellationToken cancellationToken = default) => _client.CancelAsync(Id, cancellationToken);

    /// <summary>Waits until the job succeeds, fails or is cancelled.</summary>
    /// <exception cref="TimeoutException">The job did not finish within <paramref name="timeout"/>.</exception>
    public async Task<JobInfo> WaitForCompletionAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var limit = timeout ?? TimeSpan.FromMinutes(5);
        var started = _timeProvider.GetUtcNow();
        var interval = TimeSpan.FromMilliseconds(25);

        while (true)
        {
            var job = await GetAsync(cancellationToken).AnyContext() ?? throw new InvalidOperationException($"Job {Id} no longer exists.");
            if (job.Status.IsTerminal())
                return job;

            if (_timeProvider.GetUtcNow() - started >= limit)
                throw new TimeoutException($"Job {Id} is still {job.Status} after {limit}.");

            await Task.Delay(interval, cancellationToken).AnyContext();
            interval = TimeSpan.FromMilliseconds(Math.Min(interval.TotalMilliseconds * 2, 1000));
        }
    }

    public override string ToString() => Id;
}
