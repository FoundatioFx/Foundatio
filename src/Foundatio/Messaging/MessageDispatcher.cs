using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging.Transport;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging;

/// <summary>Runs one source's handlers for each delivery, and settles it: retries, dead letters, expiry and delays.</summary>
internal sealed class MessageDispatcher : IMessageDeliveryHandler
{
    /// <summary>Dead-letter reasons are cut to this many UTF-8 bytes, so a long exception message cannot push a dead letter over a broker's size limit.</summary>
    internal const int MaxDeadLetterReasonBytes = 1024;

    private const int MaxCachedTypes = 1024;
    private static readonly TimeSpan EarlyDeliveryTolerance = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MaxDeadLetterRetryDelay = TimeSpan.FromMinutes(1);

    private readonly MessagingRuntime _runtime;
    private readonly MessageSource _source;
    private readonly IReadOnlyList<MessageHandlerRegistration> _handlers;
    private readonly ProcessingStatusTracker _status;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, IReadOnlyList<HandlerMatch>> _matches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _reportedUnknownTypes = new(StringComparer.Ordinal);
    private readonly FailedDeadLetters _failedDeadLetters = new();

    public MessageDispatcher(MessagingRuntime runtime, MessageSource source, IReadOnlyList<MessageHandlerRegistration> handlers, ProcessingStatusTracker status)
    {
        _runtime = runtime;
        _source = source;
        _handlers = handlers;
        _status = status;
        _logger = runtime.LoggerFactory.CreateLogger<MessageProcessor>();
    }

    public async Task<bool> HandleAsync(MessageDelivery delivery, CancellationToken abortToken)
    {
        var message = delivery.Message;
        var headers = message.Headers;
        string messageType = headers.GetValueOrDefault(KnownHeaders.MessageType) ?? String.Empty;
        using var logScope = _logger.BeginScope(new Dictionary<string, object> { ["message.id"] = message.MessageId, ["message.type"] = messageType });
        var now = _runtime.TimeProvider.GetUtcNow();

        if (TryGetTime(headers, KnownHeaders.ExpiresAt) is { } expiresAt && expiresAt <= now)
        {
            _logger.LogInformation("Discarding expired message {MessageId} from {Source}", message.MessageId, _source);
            MessagingInstruments.Discarded.Add(1, new KeyValuePair<string, object?>("messaging.message.type", messageType));
            bool removed = await CompleteAsync(delivery).AnyContext();
            _runtime.Observer?.OnHandled(_source, message, null, "expired", null, null);
            return removed;
        }

        if (TryGetTime(headers, KnownHeaders.DeliverAt) is { } deliverAt && deliverAt > now.Add(EarlyDeliveryTolerance) && _source.Kind == MessageSourceKind.Queue)
            return await RescheduleAsync(delivery, deliverAt).AnyContext();

        if (_failedDeadLetters.TryGetReason(message.MessageId, out string pendingReason))
            return await RetryDeadLetterAsync(delivery, pendingReason, abortToken).AnyContext();

        var matches = FindHandlers(messageType, headers);
        if (matches.Count == 0)
            return await HandleUnmatchedAsync(delivery, messageType, abortToken).AnyContext();

        var payloads = new List<(Type Type, object Payload)>(1);
        try
        {
            foreach (var match in matches)
            {
                if (!payloads.Exists(p => p.Type == match.PayloadType))
                    payloads.Add((match.PayloadType, _runtime.Serializer.Deserialize(message.Body, match.PayloadType) ?? throw new MessageBusException("The message body deserialized to null.")));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not deserialize message {MessageId} as {MessageType}", message.MessageId, messageType);
            string reason = $"Deserialization failed: {ex.Message}";
            var (deserializeOutcome, deadLetterError) = await DeadLetterOrDropAsync(delivery, reason, abortToken).AnyContext();
            _runtime.Observer?.OnHandled(_source, message, null, deserializeOutcome, reason, deadLetterError ?? ex);
            return deserializeOutcome == "dead-lettered";
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
            Source = _source.ToString()
        };

        var policy = matches[0].Handler.Options.RetryPolicy ?? _runtime.Options.DefaultRetryPolicy;
        using var handlerCancellation = CancellationTokenSource.CreateLinkedTokenSource(abortToken);
        delivery.AttachHandler(handlerCancellation);
        using var activity = StartActivity(messageType, headers);
        long started = Stopwatch.GetTimestamp();

        Exception? error = null;
        var previous = MessageContext.Current;
        MessageContext.Current = context;
        try
        {
            foreach (var match in matches)
            {
                await using var scope = _runtime.ScopeFactory.CreateAsyncScope();
                context.Services = scope.ServiceProvider;
                await match.Handler.Invoke(scope.ServiceProvider, payloads.Find(p => p.Type == match.PayloadType).Payload, context, handlerCancellation.Token).AnyContext();
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

        var (outcome, settleError) = await SettleAfterHandlingAsync(delivery, context, policy, error, abortToken).AnyContext();
        _runtime.Observer?.OnHandled(_source, message, payloads[0].Payload, outcome, context.DeadLetterReason, error ?? settleError);

        var tags = new TagList { { "messaging.message.type", messageType }, { "messaging.source", _source.Kind.ToString() }, { "outcome", outcome } };
        MessagingInstruments.Handled.Add(1, tags);
        MessagingInstruments.Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
        if (error is not null && outcome != "cancelled")
            activity?.SetErrorStatus(error);

        return outcome is "success" or "dead-lettered";
    }

    public void OnReleased(MessageDelivery delivery, string outcome)
    {
        _runtime.Observer?.OnHandled(_source, delivery.Message, null, outcome, null, null);
    }

    /// <summary>Cuts <paramref name="reason"/> to at most <see cref="MaxDeadLetterReasonBytes"/> UTF-8 bytes, without splitting a character.</summary>
    internal static string TruncateReason(string reason)
    {
        if (reason.Length * 3 <= MaxDeadLetterReasonBytes || Encoding.UTF8.GetByteCount(reason) <= MaxDeadLetterReasonBytes)
            return reason;

        const string ellipsis = "...";
        int budget = MaxDeadLetterReasonBytes - ellipsis.Length;
        int length = 0, bytes = 0;
        while (length < reason.Length)
        {
            int width = Char.IsSurrogatePair(reason, length) ? 2 : 1;
            int size = Encoding.UTF8.GetByteCount(reason.AsSpan(length, width));
            if (bytes + size > budget)
                break;

            bytes += size;
            length += width;
        }

        return String.Concat(reason.AsSpan(0, length), ellipsis);
    }

    private async Task<(string Outcome, Exception? Error)> SettleAfterHandlingAsync(MessageDelivery delivery, MessageContext context, RetryPolicy policy, Exception? error, CancellationToken abortToken)
    {
        var message = delivery.Message;
        if (delivery.IsLeaseLost)
        {
            _logger.LogWarning(error, "Lost the lease on message {MessageId}; another consumer will handle it", message.MessageId);
            return ("lease-lost", null);
        }

        if (error is OperationCanceledException && abortToken.IsCancellationRequested)
        {
            await AbandonAsync(delivery, TimeSpan.Zero).AnyContext();
            return ("cancelled", null);
        }

        if (error is null && context.DeadLetterReason is { } reason)
            return await DeadLetterOrDropAsync(delivery, reason, abortToken).AnyContext();

        if (error is null && !context.RetryRequested)
            return await CompleteAsync(delivery).AnyContext() ? ("success", null) : ("settle-failed", null);

        if (policy.CanRetry(error, context.Attempt))
        {
            var delay = context.RetryDelay ?? policy.GetDelay(context.Attempt);
            _logger.LogWarning(error, "Message {MessageId} failed on attempt {Attempt} of {MaxAttempts}; retrying in {Delay}",
                message.MessageId, context.Attempt, policy.MaxAttempts, delay);
            await RetryAsync(delivery, delay, abortToken).AnyContext();
            return ("retry", null);
        }

        string description = error is null ? "Retry requested" : $"{error.GetType().Name}: {error.Message}";
        _logger.LogError(error, "Message {MessageId} failed after {Attempt} attempts: {Reason}", message.MessageId, context.Attempt, description);
        return await DeadLetterOrDropAsync(delivery, description, abortToken).AnyContext();
    }

    /// <summary>Releases the message to be delivered again after <paramref name="delay"/>, holding it in-process when the broker cannot delay it.</summary>
    private async Task RetryAsync(MessageDelivery delivery, TimeSpan delay, CancellationToken abortToken)
    {
        var capabilities = _runtime.Transport.Capabilities;
        var maxDelay = capabilities.MaxRetryDelay;
        if (delay > TimeSpan.Zero && (maxDelay == TimeSpan.Zero || delay < capabilities.MinRetryDelay))
        {
            // The broker can't delay this retry (accurately): hold the lease, still renewed, for the backoff, then release at once.
            delivery.ResumeRenewing();
            await _runtime.TimeProvider.SafeDelay(delay, abortToken).AnyContext();
            delay = TimeSpan.Zero;
        }
        else if (maxDelay is { } max && delay > max)
        {
            delay = max;
        }

        await AbandonAsync(delivery, delay).AnyContext();
    }

    private async Task<bool> RescheduleAsync(MessageDelivery delivery, DateTimeOffset deliverAt)
    {
        var message = delivery.Message;
        var now = _runtime.TimeProvider.GetUtcNow();
        var maxDelay = _runtime.Transport.Capabilities.MaxDelay;
        var next = maxDelay is { } max && deliverAt - now > max ? now.Add(max) : deliverAt;

        try
        {
            using var timeout = new CancellationTokenSource(MessageDelivery.SettleTimeout);
            await _runtime.Transport.SendAsync(MessageDestination.Queue(_source.Name), [new OutgoingMessage
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

            await AbandonAsync(delivery, retryIn).AnyContext();
            return false;
        }

        return await CompleteAsync(delivery).AnyContext();
    }

    private async Task<bool> HandleUnmatchedAsync(MessageDelivery delivery, string messageType, CancellationToken abortToken)
    {
        var message = delivery.Message;
        MessagingInstruments.Unmatched.Add(1, new KeyValuePair<string, object?>("messaging.message.type", messageType));

        // Topics carry many message types; a subscriber ignores the ones it does not handle.
        if (_source.Kind is MessageSourceKind.Subscription or MessageSourceKind.InstanceSubscription)
        {
            if (!_runtime.Types.TryGetType(messageType, out _) && _reportedUnknownTypes.Count < MaxCachedTypes && _reportedUnknownTypes.TryAdd(messageType, 0))
            {
                _logger.LogInformation("Skipping messages of type {MessageType} on {Source}: the type is unknown here and names no base type a handler accepts. " +
                    "If a handler should receive it, have the publisher upgrade, or map the type with MapMessage", messageType, _source);
            }
            else
            {
                _logger.LogDebug("Skipping message {MessageId} of type {MessageType}: no handler on {Source}", message.MessageId, messageType, _source);
            }

            bool skipped = await CompleteAsync(delivery).AnyContext();
            _runtime.Observer?.OnHandled(_source, message, null, "skipped", null, null);
            return skipped;
        }

        // A queue is owned by its consumer, so an unknown type usually means a deployment is still rolling out.
        string reason = $"No handler for message type \"{messageType}\"";
        var policy = _runtime.Options.DefaultRetryPolicy;
        int attempt = Math.Max(1, message.DeliveryCount);
        if (attempt < policy.MaxAttempts)
        {
            _logger.LogWarning("No handler for message type {MessageType} on {Source}; retrying (attempt {Attempt} of {MaxAttempts})", messageType, _source, attempt, policy.MaxAttempts);
            await RetryAsync(delivery, policy.GetDelay(attempt), abortToken).AnyContext();
            _runtime.Observer?.OnHandled(_source, message, null, "retry", reason, null);
            return false;
        }

        _logger.LogError("No handler for message type {MessageType} on {Source}; dead-lettering message {MessageId}", messageType, _source, message.MessageId);
        var (outcome, error) = await DeadLetterOrDropAsync(delivery, reason, abortToken).AnyContext();
        _runtime.Observer?.OnHandled(_source, message, null, outcome, reason, error);
        return outcome == "dead-lettered";
    }

    /// <summary>Dead-letters a message whose earlier dead-letter failed, without running its handlers again.</summary>
    private async Task<bool> RetryDeadLetterAsync(MessageDelivery delivery, string reason, CancellationToken abortToken)
    {
        _logger.LogWarning("Dead-lettering message {MessageId} again without running its handlers: dead-lettering it failed before", delivery.Message.MessageId);
        var (outcome, error) = await DeadLetterOrDropAsync(delivery, reason, abortToken).AnyContext();
        _runtime.Observer?.OnHandled(_source, delivery.Message, null, outcome, reason, error);
        return outcome == "dead-lettered";
    }

    /// <summary>
    /// Moves the message to the dead-letter queue. If that fails, it is remembered, so a redelivery retries the
    /// dead-letter without running the handlers again, the failure shows in health, and the message is released after
    /// a growing backoff rather than waiting for its lease to expire.
    /// </summary>
    private async Task<(string Outcome, Exception? Error)> DeadLetterOrDropAsync(MessageDelivery delivery, string reason, CancellationToken abortToken)
    {
        var message = delivery.Message;
        MessagingInstruments.DeadLettered.Add(1, new KeyValuePair<string, object?>("messaging.message.type", message.Headers.GetValueOrDefault(KnownHeaders.MessageType)));

        // Instance subscriptions are best effort and have no dead-letter queue.
        if (_source.Kind == MessageSourceKind.InstanceSubscription)
            return await CompleteAsync(delivery).AnyContext() ? ("dead-lettered", null) : ("settle-failed", null);

        string truncated = TruncateReason(reason);
        var error = await delivery.SettleAsync((receiver, incoming, ct) => receiver.DeadLetterAsync(incoming, truncated, ct), "dead-letter", _logger).AnyContext();
        if (error is null)
        {
            _failedDeadLetters.Remove(message.MessageId);
            _status.RecordSettleSuccess();
            return ("dead-lettered", null);
        }

        if (error is MessageLeaseLostException)
            return ("lease-lost", error);

        int failures = _failedDeadLetters.RecordFailure(message.MessageId, truncated);
        _status.RecordSettleFailure(error, $"Could not dead-letter message {message.MessageId}");
        var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, Math.Min(failures - 1, 16)), MaxDeadLetterRetryDelay.TotalSeconds));
        _logger.LogWarning("Dead-lettering message {MessageId} failed {Failures} time(s); releasing it for {Delay}, and its handlers will not run again", message.MessageId, failures, delay);
        await RetryAsync(delivery, delay, abortToken).AnyContext();
        return ("dead-letter-failed", error);
    }

    private async Task<bool> CompleteAsync(MessageDelivery delivery)
        => await delivery.SettleAsync(static (receiver, message, ct) => receiver.CompleteAsync(message, ct), "complete", _logger).AnyContext() is null;

    private Task<Exception?> AbandonAsync(MessageDelivery delivery, TimeSpan delay)
        => delivery.SettleAsync((receiver, message, ct) => receiver.AbandonAsync(message, delay, ct), "abandon", _logger);

    /// <summary>
    /// Finds the handlers for a message and the type to deserialize it as for each. A type unknown here, published by
    /// another service, matches handlers for any of the base types and interfaces its publisher listed, and is
    /// deserialized as the most derived one each handler accepts.
    /// </summary>
    private IReadOnlyList<HandlerMatch> FindHandlers(string messageType, IReadOnlyDictionary<string, string> headers)
    {
        if (messageType.Length == 0)
            return [];

        bool known = _runtime.Types.TryGetType(messageType, out var type);
        string? baseTypes = known ? null : headers.GetValueOrDefault(KnownHeaders.MessageBaseTypes);
        string key = known ? messageType : String.Concat(messageType, "\n", baseTypes);
        if (_matches.TryGetValue(key, out var cached))
            return cached;

        var candidates = new List<Type>();
        if (known)
        {
            candidates.Add(type);
        }
        else if (baseTypes is not null)
        {
            foreach (var name in baseTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (_runtime.Types.TryGetType(name, out var baseType) && !candidates.Contains(baseType))
                    candidates.Add(baseType);
            }
        }

        var matches = new List<HandlerMatch>();
        foreach (var handler in _handlers)
        {
            var payloadType = candidates.Find(handler.MessageType.IsAssignableFrom);
            if (payloadType is not null)
                matches.Add(new HandlerMatch(handler, payloadType));
        }

        if (_matches.Count < MaxCachedTypes)
            _matches.TryAdd(key, matches);

        return matches;
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

    private readonly record struct HandlerMatch(MessageHandlerRegistration Handler, Type PayloadType);
}
