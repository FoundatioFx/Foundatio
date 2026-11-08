using System;

namespace Foundatio;

/// <summary>How many times a failed message or job is attempted, and how long to wait between attempts.</summary>
public sealed record RetryPolicy
{
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
        return this with { ShouldRetry = ex => ex is not TException && (existing?.Invoke(ex) ?? true) };
    }

    /// <summary>Waits <paramref name="initial"/>, then four times longer after each attempt, up to <paramref name="max"/>, with ±20% jitter.</summary>
    public static Func<int, TimeSpan> Exponential(TimeSpan initial, TimeSpan max)
    {
        return attempt =>
        {
            double milliseconds = Math.Min(initial.TotalMilliseconds * Math.Pow(4, Math.Max(0, attempt - 1)), max.TotalMilliseconds);
            return TimeSpan.FromMilliseconds(milliseconds * (0.8 + Random.Shared.NextDouble() * 0.4));
        };
    }

    /// <summary>Always waits <paramref name="delay"/>.</summary>
    public static Func<int, TimeSpan> Fixed(TimeSpan delay) => _ => delay;

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
