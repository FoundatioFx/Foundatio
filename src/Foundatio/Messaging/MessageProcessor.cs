using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging.Transport;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging;

/// <summary>Receives from one source and runs its handlers with bounded concurrency, lease renewal, retries and graceful shutdown.</summary>
internal sealed class MessageProcessor : IAsyncDisposable
{
    private static readonly TimeSpan ReceiveWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AbortGracePeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxReceiveBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EarlyDeliveryTolerance = TimeSpan.FromMilliseconds(50);

    private readonly MessagingRuntime _runtime;
    private readonly IReadOnlyList<MessageHandlerRegistration> _handlers;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, IReadOnlyList<MessageHandlerRegistration>> _handlersByType = new();
    private readonly int _maxConcurrency;
    private readonly TimeSpan _leaseDuration;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _stopReceiving = new();
    private readonly CancellationTokenSource _abortHandlers = new();
    private IMessageReceiver? _receiver;
    private Task _receiveLoop = Task.CompletedTask;
    private int _inFlight;
    private int _stopped;

    public MessageProcessor(MessagingRuntime runtime, MessageSource source, IReadOnlyList<MessageHandlerRegistration> handlers)
    {
        _runtime = runtime;
        Source = source;
        _handlers = handlers;
        _logger = runtime.LoggerFactory.CreateLogger<MessageProcessor>();

        var capabilities = runtime.Transport.Capabilities;
        _maxConcurrency = handlers.Max(h => h.Options.MaxConcurrency ?? Environment.ProcessorCount);
        var lease = handlers.Max(h => h.Options.LeaseDuration ?? TimeSpan.FromMinutes(1));
        _leaseDuration = capabilities.MaxLeaseDuration is { } maxLease && lease > maxLease ? maxLease : lease;
        _slots = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
    }

    public MessageSource Source { get; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var receiver = await _runtime.Transport.CreateReceiverAsync(Source, new ReceiverOptions
        {
            LeaseDuration = _leaseDuration,
            PrefetchCount = _maxConcurrency
        }, cancellationToken).AnyContext();

        if (Volatile.Read(ref _stopped) == 1)
        {
            await receiver.DisposeAsync().AnyContext();
            return;
        }

        _receiver = receiver;

        _logger.LogInformation("Receiving {Source} with {Handlers} (concurrency {MaxConcurrency})",
            Source, String.Join(", ", _handlers.Select(h => h.HandlerName).Distinct()), _maxConcurrency);
        _receiveLoop = Task.Run(ReceiveLoopAsync, CancellationToken.None);
    }

    /// <summary>
    /// Stops receiving and waits for in-flight messages to finish until <paramref name="drainTimeout"/> elapses or
    /// <paramref name="cancellationToken"/> is cancelled. Handlers still running are then cancelled and their messages released.
    /// </summary>
    public async Task StopAsync(TimeSpan drainTimeout, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
            return;

        _stopReceiving.Cancel();
        await _receiveLoop.AnyContext();

        if (!await WaitForInFlightAsync(drainTimeout, cancellationToken).AnyContext())
        {
            _logger.LogWarning("Cancelling {Count} in-flight messages from {Source} at shutdown; they will be delivered again", Volatile.Read(ref _inFlight), Source);
            _abortHandlers.Cancel();
            await WaitForInFlightAsync(AbortGracePeriod, CancellationToken.None).AnyContext();
        }

        if (_receiver is not null)
        {
            try
            {
                await _receiver.DisposeAsync().AnyContext();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error closing receiver for {Source}", Source);
            }
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync(TimeSpan.Zero));

    private async Task ReceiveLoopAsync()
    {
        var receiver = _receiver!;
        int maxBatch = Math.Max(1, _runtime.Transport.Capabilities.MaxReceiveBatchSize);
        var backoff = TimeSpan.Zero;
        var token = _stopReceiving.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await _slots.WaitAsync(token).AnyContext();
            }
            catch (OperationCanceledException)
            {
                break;
            }

            int slots = 1;
            while (slots < maxBatch && _slots.CurrentCount > 0 && await _slots.WaitAsync(0, CancellationToken.None).AnyContext())
                slots++;

            IReadOnlyList<IncomingMessage> messages;
            try
            {
                messages = await receiver.ReceiveAsync(slots, ReceiveWait, token).AnyContext();
                backoff = TimeSpan.Zero;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _slots.Release(slots);
                break;
            }
            catch (Exception ex)
            {
                _slots.Release(slots);
                backoff = backoff == TimeSpan.Zero ? TimeSpan.FromSeconds(1) : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxReceiveBackoff.Ticks));
                _logger.LogError(ex, "Error receiving from {Source}; retrying in {Backoff}", Source, backoff);
                await _runtime.TimeProvider.SafeDelay(backoff, token).AnyContext();
                continue;
            }

            if (messages.Count < slots)
                _slots.Release(slots - messages.Count);

            foreach (var sequence in SplitIntoSequences(messages))
            {
                Interlocked.Add(ref _inFlight, sequence.Count);
                _ = Task.Run(() => ProcessSequenceAsync(receiver, sequence), CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Splits a batch into sequences handled one message after another. Without ordered groups every message is its
    /// own sequence; with them, messages of the same group stay together in the order they were received.
    /// </summary>
    private List<List<IncomingMessage>> SplitIntoSequences(IReadOnlyList<IncomingMessage> messages)
    {
        var sequences = new List<List<IncomingMessage>>(messages.Count);
        Dictionary<string, List<IncomingMessage>>? groups = null;
        bool ordered = _runtime.Transport.Capabilities.OrderedGroups;

        foreach (var message in messages)
        {
            if (!ordered || message.GroupKey is not { } group)
            {
                sequences.Add([message]);
                continue;
            }

            groups ??= new Dictionary<string, List<IncomingMessage>>(StringComparer.Ordinal);
            if (groups.TryGetValue(group, out var sequence))
            {
                sequence.Add(message);
                continue;
            }

            sequence = [message];
            groups[group] = sequence;
            sequences.Add(sequence);
        }

        return sequences;
    }

    private async Task ProcessSequenceAsync(IMessageReceiver receiver, List<IncomingMessage> sequence)
    {
        bool release = false;
        foreach (var message in sequence)
        {
            try
            {
                // A later message of a group must not overtake an earlier one that will be delivered again.
                if (release || _abortHandlers.IsCancellationRequested)
                {
                    await SettleAsync(ct => receiver.AbandonAsync(message, TimeSpan.Zero, ct), message, "abandon").AnyContext();
                    release = true;
                    continue;
                }

                release = !await ProcessAsync(receiver, message).AnyContext() && sequence.Count > 1;
            }
            catch (Exception ex)
            {
                release = true;
                _logger.LogError(ex, "Unexpected error processing message {MessageId} from {Source}", message.MessageId, Source);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
                _slots.Release();
            }
        }
    }

    /// <summary>Handles and settles one message. Returns true when it left the source (completed or dead-lettered).</summary>
    private async Task<bool> ProcessAsync(IMessageReceiver receiver, IncomingMessage message)
    {
        var headers = message.Headers;
        string messageType = headers.GetValueOrDefault(KnownHeaders.MessageType) ?? String.Empty;
        using var logScope = _logger.BeginScope(new Dictionary<string, object> { ["message.id"] = message.MessageId, ["message.type"] = messageType });
        var now = _runtime.TimeProvider.GetUtcNow();

        if (TryGetTime(headers, KnownHeaders.ExpiresAt) is { } expiresAt && expiresAt <= now)
        {
            _logger.LogInformation("Discarding expired message {MessageId} from {Source}", message.MessageId, Source);
            MessagingInstruments.Discarded.Add(1, new KeyValuePair<string, object?>("messaging.message.type", messageType));
            return await SettleAsync(ct => receiver.CompleteAsync(message, ct), message, "complete").AnyContext();
        }

        if (TryGetTime(headers, KnownHeaders.DeliverAt) is { } deliverAt && deliverAt > now.Add(EarlyDeliveryTolerance) && Source.Kind == MessageSourceKind.Queue)
        {
            return await RescheduleAsync(receiver, message, deliverAt).AnyContext();
        }

        var handlers = FindHandlers(messageType, out var clrType);
        if (handlers.Count == 0)
        {
            return await HandleUnmatchedAsync(receiver, message, messageType).AnyContext();
        }

        object payload;
        try
        {
            payload = _runtime.Serializer.Deserialize(message.Body, clrType!) ?? throw new MessageBusException("The message body deserialized to null.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not deserialize message {MessageId} as {MessageType}", message.MessageId, messageType);
            return await DeadLetterOrDropAsync(receiver, message, $"Deserialization failed: {ex.Message}").AnyContext();
        }

        var context = new MessageContext
        {
            MessageId = message.MessageId,
            MessageType = messageType,
            CorrelationId = headers.GetValueOrDefault(KnownHeaders.CorrelationId),
            Attempt = Math.Max(1, message.DeliveryCount),
            SentAt = TryGetTime(headers, KnownHeaders.SentAt),
            GroupKey = message.GroupKey,
            Headers = headers,
            Source = Source.ToString()
        };

        var policy = handlers[0].Options.RetryPolicy ?? _runtime.Options.DefaultRetryPolicy;
        using var handlerCancellation = CancellationTokenSource.CreateLinkedTokenSource(_abortHandlers.Token);
        using var lease = new LeaseRenewal(this, receiver, message, handlerCancellation);
        using var activity = StartActivity(messageType, headers);
        long started = Stopwatch.GetTimestamp();

        Exception? error = null;
        var previous = MessageContext.Current;
        MessageContext.Current = context;
        try
        {
            foreach (var handler in handlers)
            {
                await using var scope = _runtime.ScopeFactory.CreateAsyncScope();
                context.Services = scope.ServiceProvider;
                await handler.Invoke(scope.ServiceProvider, payload, context, handlerCancellation.Token).AnyContext();
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            MessageContext.Current = previous;
        }

        string outcome = await SettleAfterHandlingAsync(receiver, message, context, policy, error, lease).AnyContext();
        await lease.StopAsync().AnyContext();

        var tags = new TagList { { "messaging.message.type", messageType }, { "messaging.source", Source.Kind.ToString() }, { "outcome", outcome } };
        MessagingInstruments.Handled.Add(1, tags);
        MessagingInstruments.Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
        if (error is not null && outcome != "cancelled")
            activity?.SetErrorStatus(error);

        return outcome is "success" or "dead-lettered";
    }

    private async Task<string> SettleAfterHandlingAsync(IMessageReceiver receiver, IncomingMessage message, MessageContext context, RetryPolicy policy, Exception? error, LeaseRenewal lease)
    {
        if (lease.IsLost)
        {
            _logger.LogWarning(error, "Lost the lease on message {MessageId}; another consumer will handle it", message.MessageId);
            return "lease-lost";
        }

        if (error is OperationCanceledException && _abortHandlers.IsCancellationRequested)
        {
            await SettleAsync(ct => receiver.AbandonAsync(message, TimeSpan.Zero, ct), message, "abandon").AnyContext();
            return "cancelled";
        }

        if (error is null && context.DeadLetterReason is { } reason)
            return await DeadLetterOrDropAsync(receiver, message, reason).AnyContext() ? "dead-lettered" : "settle-failed";

        if (error is null && !context.RetryRequested)
            return await SettleAsync(ct => receiver.CompleteAsync(message, ct), message, "complete").AnyContext() ? "success" : "settle-failed";

        if (policy.CanRetry(error, context.Attempt))
        {
            var delay = context.RetryDelay ?? policy.GetDelay(context.Attempt);
            _logger.LogWarning(error, "Message {MessageId} failed on attempt {Attempt} of {MaxAttempts}; retrying in {Delay}",
                message.MessageId, context.Attempt, policy.MaxAttempts, delay);
            await RetryAsync(receiver, message, delay).AnyContext();
            return "retry";
        }

        string description = error is null ? "Retry requested" : $"{error.GetType().Name}: {error.Message}";
        _logger.LogError(error, "Message {MessageId} failed after {Attempt} attempts: {Reason}", message.MessageId, context.Attempt, description);
        return await DeadLetterOrDropAsync(receiver, message, description).AnyContext() ? "dead-lettered" : "settle-failed";
    }

    private async Task RetryAsync(IMessageReceiver receiver, IncomingMessage message, TimeSpan delay)
    {
        var maxDelay = _runtime.Transport.Capabilities.MaxRetryDelay;
        if (maxDelay == TimeSpan.Zero && delay > TimeSpan.Zero)
        {
            // No native delay: hold the lease (still renewed) for the backoff, then release immediately.
            try
            {
                await _runtime.TimeProvider.SafeDelay(delay, _abortHandlers.Token).AnyContext();
            }
            catch (OperationCanceledException)
            {
            }

            delay = TimeSpan.Zero;
        }
        else if (maxDelay is { } max && delay > max)
        {
            delay = max;
        }

        await SettleAsync(ct => receiver.AbandonAsync(message, delay, ct), message, "abandon").AnyContext();
    }

    private async Task<bool> RescheduleAsync(IMessageReceiver receiver, IncomingMessage message, DateTimeOffset deliverAt)
    {
        var now = _runtime.TimeProvider.GetUtcNow();
        var maxDelay = _runtime.Transport.Capabilities.MaxDelay;
        var next = maxDelay is { } max && deliverAt - now > max ? now.Add(max) : deliverAt;

        try
        {
            using var timeout = new CancellationTokenSource(SettleTimeout);
            await _runtime.Transport.SendAsync(MessageDestination.Queue(Source.Name), [new OutgoingMessage
            {
                MessageId = message.MessageId,
                Body = message.Body,
                Headers = message.Headers,
                GroupKey = message.GroupKey,
                DeliverAt = next
            }], timeout.Token).AnyContext();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not reschedule early message {MessageId}; it will be delivered again", message.MessageId);
            var retryIn = TimeSpan.FromSeconds(30);
            if (_runtime.Transport.Capabilities.MaxRetryDelay is { } maxRetry && retryIn > maxRetry)
                retryIn = maxRetry;

            await SettleAsync(ct => receiver.AbandonAsync(message, retryIn, ct), message, "abandon").AnyContext();
            return false;
        }

        return await SettleAsync(ct => receiver.CompleteAsync(message, ct), message, "complete").AnyContext();
    }

    private async Task<bool> HandleUnmatchedAsync(IMessageReceiver receiver, IncomingMessage message, string messageType)
    {
        MessagingInstruments.Unmatched.Add(1, new KeyValuePair<string, object?>("messaging.message.type", messageType));

        // Topics carry many message types; a subscriber ignores the ones it does not handle.
        if (Source.Kind is MessageSourceKind.Subscription or MessageSourceKind.InstanceSubscription)
        {
            _logger.LogDebug("Skipping message {MessageId} of type {MessageType}: no handler on {Source}", message.MessageId, messageType, Source);
            return await SettleAsync(ct => receiver.CompleteAsync(message, ct), message, "complete").AnyContext();
        }

        // A queue is owned by its consumer, so an unknown type usually means a deployment is still rolling out.
        var policy = _runtime.Options.DefaultRetryPolicy;
        int attempt = Math.Max(1, message.DeliveryCount);
        if (attempt < policy.MaxAttempts)
        {
            _logger.LogWarning("No handler for message type {MessageType} on {Source}; retrying (attempt {Attempt} of {MaxAttempts})", messageType, Source, attempt, policy.MaxAttempts);
            await RetryAsync(receiver, message, policy.GetDelay(attempt)).AnyContext();
            return false;
        }

        _logger.LogError("No handler for message type {MessageType} on {Source}; dead-lettering message {MessageId}", messageType, Source, message.MessageId);
        return await DeadLetterOrDropAsync(receiver, message, $"No handler for message type \"{messageType}\"").AnyContext();
    }

    private Task<bool> DeadLetterOrDropAsync(IMessageReceiver receiver, IncomingMessage message, string reason)
    {
        MessagingInstruments.DeadLettered.Add(1, new KeyValuePair<string, object?>("messaging.message.type", message.Headers.GetValueOrDefault(KnownHeaders.MessageType)));

        // Instance subscriptions are best effort and have no dead-letter queue.
        if (Source.Kind == MessageSourceKind.InstanceSubscription)
            return SettleAsync(ct => receiver.CompleteAsync(message, ct), message, "complete");

        return SettleAsync(ct => receiver.DeadLetterAsync(message, reason, ct), message, "dead-letter");
    }

    private IReadOnlyList<MessageHandlerRegistration> FindHandlers(string messageType, out Type? clrType)
    {
        clrType = null;
        if (messageType.Length == 0 || !_runtime.Types.TryGetType(messageType, out var type))
            return [];

        clrType = type;
        return _handlersByType.GetOrAdd(type, t => _handlers.Where(h => h.MessageType.IsAssignableFrom(t)).ToList());
    }

    /// <summary>Settles a message on a token that is never the handler's. Returns false when settlement failed.</summary>
    private async Task<bool> SettleAsync(Func<CancellationToken, Task> settle, IncomingMessage message, string operation)
    {
        using var timeout = new CancellationTokenSource(SettleTimeout);
        try
        {
            await settle(timeout.Token).AnyContext();
            return true;
        }
        catch (MessageLeaseLostException)
        {
            _logger.LogWarning("Could not {Operation} message {MessageId}: its lease was lost and it will be delivered again", operation, message.MessageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not {Operation} message {MessageId}; it will be delivered again when its lease expires", operation, message.MessageId);
        }

        return false;
    }

    private async Task<bool> WaitForInFlightAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Volatile.Read(ref _inFlight) > 0)
        {
            if (Stopwatch.GetTimestamp() >= deadline || cancellationToken.IsCancellationRequested)
                return false;

            try
            {
                await Task.Delay(25, cancellationToken).AnyContext();
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return true;
    }

    private static DateTimeOffset? TryGetTime(IReadOnlyDictionary<string, string> headers, string name)
    {
        return headers.TryGetValue(name, out string? value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? time : null;
    }

    private static Activity? StartActivity(string messageType, IReadOnlyDictionary<string, string> headers)
    {
        ActivityContext.TryParse(headers.GetValueOrDefault(KnownHeaders.TraceParent), headers.GetValueOrDefault(KnownHeaders.TraceState), out var parent);
        var activity = FoundatioDiagnostics.ActivitySource.StartActivity($"handle {messageType}", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.message.type", messageType);
        return activity;
    }

    /// <summary>Renews a delivery's lease in the background and cancels the handler if the lease is lost.</summary>
    private sealed class LeaseRenewal : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _task = Task.CompletedTask;

        public LeaseRenewal(MessageProcessor processor, IMessageReceiver receiver, IncomingMessage message, CancellationTokenSource handlerCancellation)
        {
            if (!processor._runtime.Transport.Capabilities.LeaseExpiration)
                return;

            var interval = TimeSpan.FromTicks(processor._leaseDuration.Ticks / 2);
            _task = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        await processor._runtime.TimeProvider.SafeDelay(interval, _stop.Token).AnyContext();
                        if (_stop.IsCancellationRequested)
                            return;

                        await receiver.RenewAsync(message, _stop.Token).AnyContext();
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (MessageLeaseLostException)
                    {
                        IsLost = true;
                        handlerCancellation.Cancel();
                        return;
                    }
                    catch (Exception ex)
                    {
                        processor._logger.LogWarning(ex, "Could not renew the lease on message {MessageId}", message.MessageId);
                    }
                }
            });
        }

        public bool IsLost { get; private set; }

        public async Task StopAsync()
        {
            _stop.Cancel();
            await _task.AnyContext();
        }

        public void Dispose()
        {
            _stop.Cancel();
            _stop.Dispose();
        }
    }
}
