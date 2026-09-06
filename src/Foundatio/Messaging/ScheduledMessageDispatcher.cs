using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundatio.Messaging;

/// <summary>Dependencies and topology policy for dispatching persisted delayed messages.</summary>
public sealed record ScheduledMessageDispatcherOptions
{
    public TimeProvider? TimeProvider { get; init; }
    public ILoggerFactory? LoggerFactory { get; init; }
    public TopologyMode TopologyMode { get; init; } = TopologyMode.Ensure;
}

/// <summary>
/// Sends due messages from a scheduling store. Independent of the job worker and scheduler.
/// Delivery is at least once: a crash after sending but before settlement can resend the same application message ID.
/// </summary>
public sealed class ScheduledMessageDispatcher
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);
    private readonly IScheduledDispatchStore _store;
    private readonly IMessageTransport _transport;
    private readonly TimeProvider _timeProvider;
    private readonly TopologyMode _topologyMode;
    private readonly ILogger _logger;

    public ScheduledMessageDispatcher(IScheduledDispatchStore store, IMessageTransport transport, ScheduledMessageDispatcherOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _timeProvider = options?.TimeProvider ?? TimeProvider.System;
        _topologyMode = options?.TopologyMode ?? TopologyMode.Ensure;
        _logger = (options?.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger<ScheduledMessageDispatcher>();
    }

    /// <summary>Dispatches up to <paramref name="limit"/> due messages, claiming each only when ready to send it.</summary>
    public Task<int> DispatchDueAsync(int limit = 100, CancellationToken cancellationToken = default)
        => DispatchDueAsync(_timeProvider.GetUtcNow(), limit, cancellationToken);

    /// <summary>Dispatches messages due by the specified UTC time.</summary>
    public async Task<int> DispatchDueAsync(DateTimeOffset utcNow, int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        int completed = 0;
        for (int index = 0; index < limit; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string claim = Guid.NewGuid().ToString("N");
            var dispatches = await _store.ClaimDueDispatchesAsync(utcNow, 1, claim, Lease, cancellationToken).AnyContext();
            if (dispatches.Count == 0)
                break;

            var dispatch = dispatches[0];
            using var timeout = new CancellationTokenSource(SendTimeout, _timeProvider);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                await SendAsync(dispatch, operation.Token).WaitAsync(operation.Token).AnyContext();
                await _store.CompleteDispatchAsync(dispatch.DispatchId, claim, operation.Token).WaitAsync(operation.Token).AnyContext();
                completed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dispatch scheduled message {DispatchId}", dispatch.DispatchId);
                using var settlement = new CancellationTokenSource(TimeSpan.FromSeconds(5), _timeProvider);
                await _store.ReleaseDispatchAsync(dispatch.DispatchId, claim, utcNow.AddSeconds(30), settlement.Token)
                    .WaitAsync(settlement.Token).AnyContext();
            }
        }

        return completed;
    }

    private async Task SendAsync(ScheduledDispatchState dispatch, CancellationToken cancellationToken)
    {
        var destination = dispatch.Destination ?? throw new InvalidOperationException($"Scheduled message {dispatch.DispatchId} has no destination.");
        if (_topologyMode == TopologyMode.Validate && _transport is not ISupportsProvisioning)
            throw new NotSupportedException($"Transport {_transport.GetType().Name} cannot validate destinations.");
        if (_topologyMode != TopologyMode.None && _transport is ISupportsProvisioning provisioning)
        {
            if (_topologyMode == TopologyMode.Ensure)
                await provisioning.EnsureAsync([new DestinationDeclaration { Address = destination }], cancellationToken).AnyContext();
            else if (!await provisioning.ExistsAsync(destination, cancellationToken).AnyContext())
                throw new InvalidOperationException($"Scheduled message destination {destination} does not exist.");
        }

        await _transport.SendAsync(destination, [new TransportMessage
        {
            MessageId = dispatch.Headers.GetValueOrDefault(KnownHeaders.MessageId) ?? dispatch.DispatchId,
            Body = dispatch.Body, Headers = dispatch.Headers,
            ContentType = dispatch.Headers.GetValueOrDefault(KnownHeaders.ContentType)
        }], dispatch.Options with { DeliverAt = null }, cancellationToken).AnyContext();
    }
}
