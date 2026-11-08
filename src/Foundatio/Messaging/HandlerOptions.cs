using System;

namespace Foundatio.Messaging;

/// <summary>How a handler receives messages.</summary>
public record HandlerOptions
{
    /// <summary>How many messages this instance handles at once. Defaults to the processor count.</summary>
    public int? MaxConcurrency { get; init; }

    /// <summary>How long a delivery is leased before another consumer may get it. Renewed automatically while handling.</summary>
    public TimeSpan? LeaseDuration { get; init; }

    /// <summary>Attempts, backoff and which exceptions are not retried. Defaults to <see cref="MessagingOptions.DefaultRetryPolicy"/>.</summary>
    public RetryPolicy? RetryPolicy { get; init; }

    internal void Validate()
    {
        if (MaxConcurrency is { } concurrency)
            ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1, nameof(MaxConcurrency));
        if (LeaseDuration is { } lease)
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero, nameof(LeaseDuration));
    }
}
