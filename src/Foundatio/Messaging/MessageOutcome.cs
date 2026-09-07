using System;
using System.Threading;
using System.Threading.Tasks;

namespace Foundatio.Messaging;

/// <summary>A handler's delivery decision. Returning an expected failure does not require throwing an exception.</summary>
public readonly record struct MessageOutcome
{
    private MessageOutcome(MessageOutcomeKind kind, string? reason, Exception? exception)
    {
        Kind = kind;
        Reason = reason;
        Exception = exception;
    }

    /// <summary>The requested settlement. An already confirmed explicit settlement takes precedence.</summary>
    public MessageOutcomeKind Kind { get; }
    /// <summary>A diagnostic reason retained when dead-lettering.</summary>
    public string? Reason { get; }
    /// <summary>Optional exception retained as dead-letter evidence.</summary>
    public Exception? Exception { get; }
    /// <summary>Processing succeeded; automatic acknowledgement applies.</summary>
    public static MessageOutcome Success => default;
    /// <summary>Leave the delivery unsettled for its lease to expire.</summary>
    public static MessageOutcome Unsettled => new(MessageOutcomeKind.Unsettled, null, null);
    /// <summary>Retry using the endpoint policy, or dead-letter when its attempt budget is exhausted.</summary>
    public static MessageOutcome Retry(string? reason = null, Exception? exception = null) => new(MessageOutcomeKind.Retry, reason, exception);
    /// <summary>Dead-letter immediately with the supplied reason.</summary>
    public static MessageOutcome DeadLetter(string reason, Exception? exception = null) => new(MessageOutcomeKind.DeadLetter, reason, exception);

    internal Task SettleFailureAsync(IMessageContext context, int maxAttempts, Func<int, TimeSpan>? backoff, CancellationToken cancellationToken)
    {
        if (context.IsHandled)
            return Task.CompletedTask;
        bool terminal = Kind == MessageOutcomeKind.DeadLetter || maxAttempts >= 0 && context.Attempts >= maxAttempts;
        return context.RejectAsync(new RejectOptions
        {
            Terminal = terminal,
            Reason = Reason,
            Exception = Exception,
            RedeliveryDelay = terminal ? null : backoff?.Invoke(context.Attempts),
            BestEffortDelay = true
        }, cancellationToken);
    }
}

/// <summary>The delivery decision returned by an outcome handler.</summary>
public enum MessageOutcomeKind
{
    Success,
    Retry,
    DeadLetter,
    Unsettled
}
