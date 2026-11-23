using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging.Transport;
using Foundatio.Monitoring;
using Foundatio.Resilience;
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
    private readonly string _sourceName;
    private readonly string _system;
    private readonly string _destinationName;
    private readonly string? _subscriptionName;
    private readonly string _sourceKind;
    private readonly string _activityName;
    private readonly IReadOnlyList<MessageHandlerRegistration> _handlers;
    private readonly ProcessingStatusTracker _status;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, HandlerSet> _matches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WorkCounter> _counters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _reportedUnknownTypes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _reportedUnresolvedTypes = new(StringComparer.Ordinal);
    private readonly FailedDeadLetters _failedDeadLetters = new();

    public MessageDispatcher(MessagingRuntime runtime, MessageSource source, IReadOnlyList<MessageHandlerRegistration> handlers, ProcessingStatusTracker status)
    {
        _runtime = runtime;
        _source = source;
        _sourceName = source.ToString();
        _system = MessagingInstruments.GetSystemName(runtime.Transport);
        _destinationName = source.Topic ?? source.Name;
        _subscriptionName = source.Kind == MessageSourceKind.Subscription ? source.Name : null;
        _sourceKind = MessagingInstruments.GetSourceKind(source);
        _activityName = String.Concat("process ", _destinationName);
        _handlers = handlers;
        _status = status;
        _logger = runtime.LoggerFactory.CreateLogger<MessageProcessor>();
    }

    public async Task<bool> HandleAsync(MessageDelivery delivery, CancellationToken abortToken)
    {
        var message = delivery.Message;
        var headers = message.Headers;
        string messageType = headers.GetValueOrDefault(KnownHeaders.MessageType) ?? String.Empty;
        using var logScope = _logger.BeginScope(new LogScopeState("message.id", message.MessageId, "message.type", messageType));
        var now = _runtime.TimeProvider.GetUtcNow();
        var counter = GetCounter(messageType);

        if (TryGetTime(headers, KnownHeaders.ExpiresAt) is { } expiresAt && expiresAt <= now)
        {
            _logger.LogInformation("Discarding expired message {MessageId} from {Source}", message.MessageId, _source);
            if (MessagingInstruments.Discarded.Enabled)
                MessagingInstruments.Discarded.Add(1, CreateTags(messageType));
            bool removed = await CompleteAsync(delivery).AnyContext();
            _runtime.Observer?.OnHandled(_source, message, null, "expired", null, null);
            if (removed)
                RecordUnhandled(counter, message, "expired", null);

            return removed;
        }

        if (TryGetTime(headers, KnownHeaders.DeliverAt) is { } deliverAt && deliverAt > now.Add(EarlyDeliveryTolerance) && _source.Kind == MessageSourceKind.Queue)
            return await RescheduleAsync(delivery, deliverAt).AnyContext();

        if (_failedDeadLetters.TryGetReason(message.MessageId, out string pendingReason))
            return await RetryDeadLetterAsync(delivery, counter, pendingReason, abortToken).AnyContext();

        var handlerSet = FindHandlers(messageType, headers);
        if (handlerSet.Unresolved is { } unresolved)
            ReportUnresolved(messageType, unresolved);

        var matches = handlerSet.Matches;
        if (matches.Length == 0)
            return await HandleUnmatchedAsync(delivery, counter, messageType, handlerSet.Unresolved is not null, abortToken).AnyContext();

        // One payload per type the handlers take; usually a single handler, so a single payload.
        var payloads = new (Type Type, object Payload)[matches.Length];
        int payloadCount = 0;
        try
        {
            foreach (var match in matches)
            {
                if (FindPayload(payloads, payloadCount, match.PayloadType) is null)
                    payloads[payloadCount++] = (match.PayloadType, _runtime.Serializer.Deserialize(message.Body, match.PayloadType) ?? throw new MessageBusException("The message body deserialized to null."));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not deserialize message {MessageId} as {MessageType}", message.MessageId, messageType);
            string reason = $"Deserialization failed: {ex.Message}";
            var (deserializeOutcome, deadLetterError) = await DeadLetterOrDropAsync(delivery, reason, abortToken).AnyContext();
            _runtime.Observer?.OnHandled(_source, message, null, deserializeOutcome, reason, deadLetterError ?? ex);
            RecordUnhandled(counter, message, deserializeOutcome, reason);
            return deserializeOutcome == "dead-lettered";
        }

        using var activity = StartActivity(message, messageType);
        var context = new MessageContext
        {
            MessageId = message.MessageId,
            MessageType = messageType,
            CorrelationId = headers.GetValueOrDefault(KnownHeaders.CorrelationId),
            Attempt = Math.Max(1, message.DeliveryCount),
            GroupKey = message.GroupKey,
            Headers = headers,
            Source = _sourceName,
            Activity = activity,
            Logger = _logger
        };
        context.ParseSentAtOnDemand(headers);

        var policy = matches[0].Handler.Options.RetryPolicy ?? _runtime.Options.DefaultRetryPolicy;
        using var handlerCancellation = CancellationTokenSource.CreateLinkedTokenSource(abortToken);
        delivery.AttachHandler(handlerCancellation);
        long started = Stopwatch.GetTimestamp();

        Exception? error = null;
        var previous = MessageContext.Current;
        MessageContext.Current = context;
        _runtime.Tracker.Start(context, counter, handlerSet.Names);
        try
        {
            foreach (var match in matches)
            {
                var payload = FindPayload(payloads, payloadCount, match.PayloadType)!;
                if (match.Handler.ResolvesFromScope)
                {
                    await using var scope = _runtime.ScopeFactory.CreateAsyncScope();
                    context.Services = scope.ServiceProvider;
                    await match.Handler.Invoke(scope.ServiceProvider, payload, context, handlerCancellation.Token).AnyContext();
                    _runtime.Observer?.OnHandlerCompleted(context, match.Handler);
                    continue;
                }

                // A delegate gets a scope only if it reads context.Services.
                context.UseScopeOnDemand(_runtime.ScopeFactory);
                try
                {
                    await match.Handler.Invoke(Testing.EmptyServiceProvider.Instance, payload, context, handlerCancellation.Token).AnyContext();
                }
                finally
                {
                    await context.DisposeScopeAsync().AnyContext();
                }

                _runtime.Observer?.OnHandlerCompleted(context, match.Handler);
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            MessageContext.Current = previous;
            _runtime.Tracker.Stop(context);
        }

        var (outcome, settleError) = await SettleAfterHandlingAsync(delivery, context, policy, error, abortToken).AnyContext();
        _runtime.Observer?.OnHandled(_source, message, payloads[0].Payload, outcome, context.DeadLetterReason, error ?? settleError, context);
        _runtime.Tracker.Complete(context, ToWorkOutcome(outcome), DescribeError(outcome, context, error));

        bool failed = error is not null && outcome != "cancelled";
        if (MessagingInstruments.Handled.Enabled || MessagingInstruments.Duration.Enabled)
        {
            var tags = CreateTags(messageType);
            tags.Add(MessagingInstruments.OperationName, "process");
            tags.Add(MessagingInstruments.SourceKind, _sourceKind);
            tags.Add(MessagingInstruments.Outcome, outcome);
            if (failed)
                tags.Add(MessagingInstruments.ErrorType, GetErrorType(error!));

            MessagingInstruments.Handled.Add(1, tags);
            MessagingInstruments.Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
        }
        if (failed && activity is not null)
        {
            activity.SetTag(MessagingInstruments.ErrorType, GetErrorType(error!));
            activity.SetErrorStatus(error);
        }

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

    private async ValueTask<(string Outcome, Exception? Error)> SettleAfterHandlingAsync(MessageDelivery delivery, MessageContext context, RetryPolicy policy, Exception? error, CancellationToken abortToken)
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
        {
            _logger.LogWarning("{MessageType} message {MessageId} from {Source} was dead-lettered by its handler on attempt {Attempt}: {Reason}",
                context.MessageType, message.MessageId, _sourceName, context.Attempt, reason);
            return await DeadLetterOrDropAsync(delivery, reason, abortToken).AnyContext();
        }

        if (error is null && !context.RetryRequested)
            return await CompleteAsync(delivery).AnyContext() ? ("success", null) : ("settle-failed", null);

        string description = error is null ? "Retry requested" : $"{error.GetType().Name}: {error.Message}";
        TimeSpan? retryIn;
        try
        {
            // The test harness's RetryDelay also replaces a delay the handler asked for.
            retryIn = policy.CanRetry(error, context.Attempt) ? context.RetryDelay is { } requested ? _runtime.Options.TestRetryDelay ?? requested : policy.GetDelay(context.Attempt) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The retry policy for message {MessageId} threw; dead-lettering it", message.MessageId);
            return await DeadLetterOrDropAsync(delivery, $"The retry policy threw {ex.GetType().Name}: {ex.Message} while handling {description}", abortToken).AnyContext();
        }

        if (retryIn is { } delay)
        {
            _logger.LogWarning(error, "{MessageType} message {MessageId} from {Source} failed on attempt {Attempt} of {MaxAttempts}; retrying in {RetryDelay}",
                context.MessageType, message.MessageId, _sourceName, context.Attempt, policy.MaxAttempts, FormatDelay(delay));
            await RetryAsync(delivery, delay, abortToken).AnyContext();
            return (delivery.IsLeaseLost ? "lease-lost" : "retry", null);
        }

        _logger.LogError(error, "{MessageType} message {MessageId} from {Source} failed after {Attempt} attempts and is dead-lettered: {Reason}",
            context.MessageType, message.MessageId, _sourceName, context.Attempt, description);
        return await DeadLetterOrDropAsync(delivery, description, abortToken).AnyContext();
    }

    /// <summary>
    /// Releases the message to be delivered again after <paramref name="delay"/>. A delay within a fifth of the
    /// transport's <see cref="TransportCapabilities.MinRetryDelay"/> is lengthened to it; one the broker cannot delay
    /// is held in-process, and the message then released at once.
    /// </summary>
    private async Task RetryAsync(MessageDelivery delivery, TimeSpan delay, CancellationToken abortToken)
    {
        var capabilities = _runtime.Transport.Capabilities;
        var maxDelay = capabilities.MaxRetryDelay;
        var minDelay = capabilities.MinRetryDelay;

        // The default policy's jitter puts about half of its first retries just under a 1 s minimum.
        if (delay > TimeSpan.Zero && maxDelay != TimeSpan.Zero && delay < minDelay && delay.Ticks >= minDelay.Ticks - minDelay.Ticks / 5)
            delay = minDelay;

        if (delay > TimeSpan.Zero && (maxDelay == TimeSpan.Zero || delay < minDelay))
        {
            // A lease lost meanwhile ends the hold, and the message is not released: another consumer may have it.
            await delivery.HoldAsync(delay, abortToken).AnyContext();
            if (delivery.IsLeaseLost)
                return;

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

    /// <summary>
    /// Handles a message no handler here can take. With <paramref name="unresolved"/>, handlers accept a base type of
    /// it, but only abstract or interface ones, so there is no type to deserialize it as.
    /// </summary>
    private async Task<bool> HandleUnmatchedAsync(MessageDelivery delivery, WorkCounter counter, string messageType, bool unresolved, CancellationToken abortToken)
    {
        var message = delivery.Message;
        if (MessagingInstruments.Unmatched.Enabled)
            MessagingInstruments.Unmatched.Add(1, CreateTags(messageType));

        // Topics carry many message types; a subscriber ignores the ones it does not handle.
        if (_source.Kind is MessageSourceKind.Subscription or MessageSourceKind.InstanceSubscription)
        {
            if (!unresolved && !_runtime.Types.TryGetType(messageType, out _) && _reportedUnknownTypes.Count < MaxCachedTypes && _reportedUnknownTypes.TryAdd(messageType, 0))
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
            if (skipped)
                RecordUnhandled(counter, message, "skipped", null);

            return skipped;
        }

        // A queue is owned by its consumer, so an unknown type usually means a deployment is still rolling out.
        string reason = unresolved
            ? $"No concrete type for message type \"{messageType}\": its handlers here accept only abstract or interface types it derives from"
            : $"No handler for message type \"{messageType}\"";
        var policy = _runtime.Options.DefaultRetryPolicy;
        int attempt = Math.Max(1, message.DeliveryCount);
        TimeSpan? retryIn = null;
        if (attempt < policy.MaxAttempts)
        {
            try
            {
                retryIn = policy.GetDelay(attempt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The default retry policy threw; dead-lettering message {MessageId}", message.MessageId);
                reason = $"{reason}; the default retry policy threw {ex.GetType().Name}: {ex.Message}";
            }
        }

        if (retryIn is { } delay)
        {
            _logger.LogWarning("No handler for message type {MessageType} on {Source}; retrying (attempt {Attempt} of {MaxAttempts})", messageType, _source, attempt, policy.MaxAttempts);
            await RetryAsync(delivery, delay, abortToken).AnyContext();
            _runtime.Observer?.OnHandled(_source, message, null, "retry", reason, null);
            RecordUnhandled(counter, message, "retry", reason);
            return false;
        }

        _logger.LogError("No handler for message type {MessageType} on {Source}; dead-lettering message {MessageId}", messageType, _source, message.MessageId);
        var (outcome, error) = await DeadLetterOrDropAsync(delivery, reason, abortToken).AnyContext();
        _runtime.Observer?.OnHandled(_source, message, null, outcome, reason, error);
        RecordUnhandled(counter, message, outcome, reason);
        return outcome == "dead-lettered";
    }

    /// <summary>Dead-letters a message whose earlier dead-letter failed, without running its handlers again.</summary>
    private async Task<bool> RetryDeadLetterAsync(MessageDelivery delivery, WorkCounter counter, string reason, CancellationToken abortToken)
    {
        _logger.LogWarning("Dead-lettering message {MessageId} again without running its handlers: dead-lettering it failed before", delivery.Message.MessageId);
        var (outcome, error) = await DeadLetterOrDropAsync(delivery, reason, abortToken).AnyContext();
        _runtime.Observer?.OnHandled(_source, delivery.Message, null, outcome, reason, error);
        RecordUnhandled(counter, delivery.Message, outcome, reason);
        return outcome == "dead-lettered";
    }

    /// <summary>
    /// Moves the message to the dead-letter queue. If that fails, it is remembered, so a redelivery retries the
    /// dead-letter without running the handlers again, the failure shows in health, and the message is released after
    /// a growing backoff rather than waiting for its lease to expire.
    /// </summary>
    private async ValueTask<(string Outcome, Exception? Error)> DeadLetterOrDropAsync(MessageDelivery delivery, string reason, CancellationToken abortToken)
    {
        var message = delivery.Message;
        if (MessagingInstruments.DeadLettered.Enabled)
            MessagingInstruments.DeadLettered.Add(1, CreateTags(message.Headers.GetValueOrDefault(KnownHeaders.MessageType) ?? String.Empty));

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

    /// <summary>Warns, once per message type, that handlers miss a message type because they accept only abstract or interface types of it.</summary>
    private void ReportUnresolved(string messageType, string unresolved)
    {
        if (_reportedUnresolvedTypes.Count >= MaxCachedTypes || !_reportedUnresolvedTypes.TryAdd(messageType, 0))
            return;

        _logger.LogWarning("{Handlers} on {Source} cannot receive messages of type {MessageType}: the type is unknown here, and the only base types of it they accept " +
            "are abstract or interfaces, which cannot be deserialized. Define {MessageType} in the assembly of its base type, such as a contracts assembly both services share, " +
            "or map a concrete type to it here with MapMessage<T>(\"{MessageType}\")", unresolved, _source, messageType, messageType, messageType);
    }

    /// <summary>
    /// Finds the handlers for a message and the type to deserialize it as for each. A type unknown here, published by
    /// another service, matches handlers for any of the base types and interfaces its publisher listed, and is
    /// deserialized as the most derived concrete one each handler accepts. A handler that accepts only abstract or
    /// interface candidates does not match, since there is nothing to deserialize the message as.
    /// </summary>
    private HandlerSet FindHandlers(string messageType, IReadOnlyDictionary<string, string> headers)
    {
        if (messageType.Length == 0)
            return HandlerSet.None;

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
        List<string>? unresolved = null;
        foreach (var handler in _handlers)
        {
            var accepted = candidates.FindAll(handler.MessageType.IsAssignableFrom);
            if (accepted.Find(t => !t.IsAbstract && !t.IsInterface) is { } payloadType)
                matches.Add(new HandlerMatch(handler, payloadType));
            else if (accepted.Count > 0 && !(unresolved ??= []).Contains(handler.HandlerName))
                unresolved.Add(handler.HandlerName);
        }

        var handlerSet = new HandlerSet([.. matches], String.Join(", ", matches.Select(m => m.Handler.HandlerName).Distinct()), unresolved is null ? null : String.Join(", ", unresolved));
        if (_matches.Count < MaxCachedTypes)
            _matches.TryAdd(key, handlerSet);

        return handlerSet;
    }

    private static DateTimeOffset? TryGetTime(IReadOnlyDictionary<string, string> headers, string name)
    {
        return headers.TryGetValue(name, out string? value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? time : null;
    }

    private static object? FindPayload((Type Type, object Payload)[] payloads, int count, Type type)
    {
        for (int i = 0; i < count; i++)
        {
            if (payloads[i].Type == type)
                return payloads[i].Payload;
        }

        return null;
    }

    private Activity? StartActivity(IncomingMessage message, string messageType)
    {
        // Without a listener no activity starts, so skip parsing the parent.
        if (!FoundatioDiagnostics.ActivitySource.HasListeners())
            return null;

        var headers = message.Headers;
        ActivityContext.TryParse(headers.GetValueOrDefault(KnownHeaders.TraceParent), headers.GetValueOrDefault(KnownHeaders.TraceState), out var parent);
        var activity = FoundatioDiagnostics.ActivitySource.StartActivity(_activityName, ActivityKind.Consumer, parent);
        if (activity is not { IsAllDataRequested: true })
            return activity;

        activity.SetTag(MessagingInstruments.System, _system);
        activity.SetTag(MessagingInstruments.OperationType, "process");
        activity.SetTag(MessagingInstruments.OperationName, "process");
        activity.SetTag(MessagingInstruments.DestinationName, _destinationName);
        if (_subscriptionName is not null)
        {
            activity.SetTag(MessagingInstruments.SubscriptionName, _subscriptionName);
            activity.SetTag(MessagingInstruments.ConsumerGroupName, _subscriptionName);
        }

        activity.SetTag(MessagingInstruments.MessageId, message.MessageId);
        if (headers.GetValueOrDefault(KnownHeaders.CorrelationId) is { } correlationId)
            activity.SetTag(MessagingInstruments.ConversationId, correlationId);
        activity.SetTag(MessagingInstruments.MessageType, messageType);
        activity.SetTag(MessagingInstruments.SourceKind, _sourceKind);
        return activity;
    }

    /// <summary>A retry delay as seconds, such as <c>0.12s</c>, for logs.</summary>
    private static string FormatDelay(TimeSpan delay) => String.Concat(delay.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), "s");

    /// <summary>The metric attributes every messaging instrument of this source shares.</summary>
    private TagList CreateTags(string messageType)
    {
        var tags = new TagList
        {
            { MessagingInstruments.System, _system },
            { MessagingInstruments.DestinationName, _destinationName },
            { MessagingInstruments.MessageType, messageType }
        };
        if (_subscriptionName is not null)
            tags.Add(MessagingInstruments.SubscriptionName, _subscriptionName);

        return tags;
    }

    private static string GetErrorType(Exception error) => error.GetType().FullName ?? error.GetType().Name;

    /// <summary>The counter for <paramref name="messageType"/> on this source, cached like the handler matches.</summary>
    private WorkCounter GetCounter(string messageType)
    {
        if (_counters.TryGetValue(messageType, out var counter))
            return counter;

        counter = _runtime.Tracker.GetCounter(WorkKind.Message, messageType, _sourceName);
        if (_counters.Count < MaxCachedTypes)
            _counters.TryAdd(messageType, counter);

        return counter;
    }

    /// <summary>Counts a delivery that ended without its handlers running.</summary>
    private void RecordUnhandled(WorkCounter counter, IncomingMessage message, string outcome, string? reason)
    {
        _runtime.Tracker.Record(counter, ToWorkOutcome(outcome), message.MessageId, Math.Max(1, message.DeliveryCount),
            message.Headers.GetValueOrDefault(KnownHeaders.CorrelationId), reason);
    }

    /// <summary>Maps an outcome as metrics name it to how monitoring counts it.</summary>
    internal static WorkOutcome ToWorkOutcome(string outcome) => outcome switch
    {
        "success" => WorkOutcome.Succeeded,
        "retry" => WorkOutcome.Retried,
        "dead-lettered" => WorkOutcome.DeadLettered,
        "skipped" or "expired" => WorkOutcome.Skipped,
        "settle-failed" or "dead-letter-failed" => WorkOutcome.Failed,
        _ => WorkOutcome.Cancelled
    };

    private static string? DescribeError(string outcome, MessageContext context, Exception? error) => outcome switch
    {
        "success" => null,
        "lease-lost" => "The lease was lost; another consumer will handle the message",
        "cancelled" => "Interrupted by shutdown; the message will be delivered again",
        "settle-failed" => "Could not complete the message; it will be delivered again",
        _ => context.DeadLetterReason ?? (error is null ? "Retry requested" : $"{error.GetType().Name}: {error.Message}")
    };

    private readonly record struct HandlerMatch(MessageHandlerRegistration Handler, Type PayloadType);

    /// <summary>
    /// The handlers for one message type, the names monitoring shows for them, and the names of handlers that accept
    /// the type only through abstract or interface base types, so cannot receive it.
    /// </summary>
    /// <remarks>An array, so iterating it for each message boxes no enumerator.</remarks>
    private sealed record HandlerSet(HandlerMatch[] Matches, string Names, string? Unresolved = null)
    {
        public static readonly HandlerSet None = new([], String.Empty);
    }
}
