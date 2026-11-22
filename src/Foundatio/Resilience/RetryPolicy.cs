using System;
using System.Collections.Concurrent;

namespace Foundatio.Resilience;

/// <summary>How many times a failed message or job is attempted, and how long to wait between attempts.</summary>
/// <remarks>
/// Policies compare by value, delegates included. <see cref="Exponential"/>, <see cref="Fixed"/> and
/// <see cref="DoNotRetry{TException}"/> return the same delegate for the same arguments, so two policies built the same
/// way are equal: <c>new RetryPolicy { MaxAttempts = 3 } == new RetryPolicy { MaxAttempts = 3 }</c>. Policies with
/// delegates you write yourself are equal only when they share the delegate instance.
/// </remarks>
public sealed record RetryPolicy
{
    // Bounded, so code that builds policies from ever-changing values cannot grow them without limit.
    private const int MaxCachedDelegates = 256;
    private static readonly ConcurrentDictionary<(TimeSpan Initial, TimeSpan Max), Func<int, TimeSpan>> ExponentialDelays = new();
    private static readonly ConcurrentDictionary<TimeSpan, Func<int, TimeSpan>> FixedDelays = new();
    private static readonly ConcurrentDictionary<(Func<Exception, bool>? Existing, Type Exception), Func<Exception, bool>> RetryFilters = new();

    /// <summary>Five attempts, waiting about 1s, 4s, 16s and 64s between them.</summary>
    public static RetryPolicy Default { get; } = new();

    /// <summary>A single attempt.</summary>
    public static RetryPolicy None { get; } = new() { MaxAttempts = 1 };

    /// <summary>Total attempts, including the first.</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>The wait after the given failed attempt (1-based).</summary>
    public Func<int, TimeSpan> Delay { get; init; } = Exponential(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10));

    /// <summary>Return false for exceptions that should not be retried. All exceptions are retried by default.</summary>
    public Func<Exception, bool>? ShouldRetry { get; init; }

    /// <summary>Returns a copy that fails immediately on <typeparamref name="TException"/>.</summary>
    public RetryPolicy DoNotRetry<TException>() where TException : Exception
    {
        var existing = ShouldRetry;
        var key = (existing, typeof(TException));
        if (!RetryFilters.TryGetValue(key, out var filter))
            filter = Cache(RetryFilters, key, ex => ex is not TException && (existing?.Invoke(ex) ?? true));

        return this with { ShouldRetry = filter };
    }

    /// <summary>Waits <paramref name="initial"/>, then four times longer after each attempt, up to <paramref name="max"/>, with ±20% jitter.</summary>
    public static Func<int, TimeSpan> Exponential(TimeSpan initial, TimeSpan max)
    {
        if (ExponentialDelays.TryGetValue((initial, max), out var cached))
            return cached;

        return Cache(ExponentialDelays, (initial, max), attempt =>
        {
            double milliseconds = Math.Min(initial.TotalMilliseconds * Math.Pow(4, Math.Max(0, attempt - 1)), max.TotalMilliseconds);
            return TimeSpan.FromMilliseconds(milliseconds * (0.8 + Random.Shared.NextDouble() * 0.4));
        });
    }

    /// <summary>Always waits <paramref name="delay"/>.</summary>
    public static Func<int, TimeSpan> Fixed(TimeSpan delay)
    {
        return FixedDelays.TryGetValue(delay, out var cached) ? cached : Cache(FixedDelays, delay, _ => delay);
    }

    private static TDelegate Cache<TKey, TDelegate>(ConcurrentDictionary<TKey, TDelegate> cache, TKey key, TDelegate created) where TKey : notnull
    {
        return cache.Count < MaxCachedDelegates ? cache.GetOrAdd(key, created) : created;
    }

    internal bool CanRetry(Exception? exception, int attempt)
    {
        if (attempt >= MaxAttempts)
            return false;

        return exception is null || ShouldRetry is null || ShouldRetry(exception);
    }

    internal TimeSpan GetDelay(int attempt)
    {
        var delay = Delay(attempt);
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxAttempts, 1, nameof(MaxAttempts));
        ArgumentNullException.ThrowIfNull(Delay, nameof(Delay));
    }
}
