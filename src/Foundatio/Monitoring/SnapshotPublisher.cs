using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Foundatio.Monitoring;

/// <summary>
/// Writes this instance's snapshot to the cache every <see cref="MonitoringOptions.SnapshotInterval"/>, and sooner (at
/// most about once a second) when visible work changes. A failed write is logged and retried about a second later; it
/// never reaches message or job processing. On shutdown it removes the snapshot, so the instance leaves the monitor at once.
/// </summary>
internal sealed class SnapshotPublisher : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan MinSpacing = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RemoveTimeout = TimeSpan.FromSeconds(5);

    private readonly MonitoringRuntime _runtime;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task _loop = Task.CompletedTask;
    private bool _published;
    private int _stopped;

    public SnapshotPublisher(MonitoringRuntime runtime)
    {
        _runtime = runtime;
        _logger = runtime.LoggerFactory.CreateLogger<SnapshotPublisher>();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_runtime.HasCache || !_runtime.HasWork)
            return Task.CompletedTask;

        // Resolving the cache here fails the host's start when it is missing, rather than failing every write silently.
        _ = _runtime.Cache;
        _loop = Task.Run(RunAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
            return;

        await _stopping.CancelAsync().AnyContext();
        await _loop.AnyContext();
        if (_published && _runtime.Cache is { } cache)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RemoveTimeout);
            try
            {
                await Task.WhenAll(
                    cache.RemoveAsync(_runtime.GetInstanceKey(_runtime.InstanceId)),
                    cache.ListRemoveAsync(_runtime.IndexKey, [_runtime.InstanceId])).WaitAsync(timeout.Token).AnyContext();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not remove the monitoring snapshot of instance {InstanceId}; it expires on its own", _runtime.InstanceId);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            await _stopping.CancelAsync().AnyContext();
            await _loop.AnyContext();
        }

        _stopping.Dispose();
    }

    private async Task RunAsync()
    {
        var options = _runtime.Options;
        var tracker = _runtime.Tracker;
        var timeProvider = _runtime.TimeProvider;
        var tick = options.SnapshotInterval < MinSpacing ? options.SnapshotInterval : MinSpacing;
        long lastVersion = 0, lastTimestamp = 0, lastAttempt = 0;
        bool failing = false;
        var token = _stopping.Token;

        while (!token.IsCancellationRequested)
        {
            long now = timeProvider.GetTimestamp();
            bool due = lastAttempt == 0 || failing || timeProvider.GetElapsedTime(lastAttempt, now) >= options.SnapshotInterval;
            if (due || tracker.HasChangedSince(lastVersion, lastTimestamp))
            {
                long version = tracker.Version;
                lastAttempt = now;
                try
                {
                    await PublishAsync().AnyContext();
                    lastVersion = version;
                    lastTimestamp = now;
                    _published = true;
                    if (failing)
                        _logger.LogInformation("Writing monitoring snapshots works again");

                    failing = false;
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    if (!failing)
                        _logger.LogWarning(ex, "Could not write the monitoring snapshot of instance {InstanceId}; retrying", _runtime.InstanceId);
                    else
                        _logger.LogDebug(ex, "Could not write the monitoring snapshot of instance {InstanceId}; retrying", _runtime.InstanceId);

                    failing = true;
                }
                catch (Exception) when (token.IsCancellationRequested)
                {
                    break;
                }
            }

            await timeProvider.SafeDelay(tick, token).AnyContext();
        }
    }

    private async Task PublishAsync()
    {
        var cache = _runtime.Cache!;
        var expiry = _runtime.Options.EffectiveSnapshotExpiry;
        string json = JsonSerializer.Serialize(_runtime.CreateSnapshot(), MonitoringJsonContext.Default.InstanceSnapshot);
        await cache.SetAsync(_runtime.GetInstanceKey(_runtime.InstanceId), json, expiry).AnyContext();
        await cache.ListAddAsync(_runtime.IndexKey, [_runtime.InstanceId], expiry).AnyContext();
    }
}
