using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.Utility;

namespace Foundatio.Testing;

/// <summary>An <see cref="IMessageContext"/> for unit testing a handler. Records the outcome the handler chose and the progress it reported.</summary>
/// <example>
/// <code>
/// var context = new TestMessageContext();
/// await new OrderPlacedHandler(...).HandleAsync(new OrderPlaced(1), context, CancellationToken.None);
/// Assert.Null(context.DeadLetterReason);
/// Assert.Equal(100, context.Progress);
/// </code>
/// </example>
public sealed class TestMessageContext : IMessageContext
{
    private readonly List<(int? Percent, string? Message)> _reports = [];

    /// <inheritdoc />
    public string MessageId { get; init; } = Guid.NewGuid().ToString("N");
    /// <inheritdoc />
    public string MessageType { get; init; } = "test";
    /// <inheritdoc />
    public string? CorrelationId { get; init; }
    /// <inheritdoc />
    public int Attempt { get; init; } = 1;
    /// <inheritdoc />
    public DateTimeOffset? SentAt { get; init; }
    /// <inheritdoc />
    public string? GroupKey { get; init; }
    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    /// <inheritdoc />
    public string Source { get; init; } = "queue:test";
    /// <inheritdoc />
    public IServiceProvider Services { get; init; } = EmptyServiceProvider.Instance;

    /// <summary>Set when the handler called <see cref="DeadLetter"/>.</summary>
    public string? DeadLetterReason { get; private set; }

    /// <summary>True when the handler called <see cref="Retry"/>.</summary>
    public bool RetryRequested { get; private set; }

    /// <summary>The delay the handler passed to <see cref="Retry"/>, or null.</summary>
    public TimeSpan? RetryDelay { get; private set; }

    /// <summary>Every call to <see cref="ReportProgressAsync"/>, in order.</summary>
    public IReadOnlyList<(int? Percent, string? Message)> ProgressReports
    {
        get
        {
            lock (_reports)
                return _reports.ToArray();
        }
    }

    /// <summary>The last reported percentage.</summary>
    public int? Progress { get; private set; }

    /// <summary>The last reported progress message.</summary>
    public string? ProgressMessage { get; private set; }

    /// <summary>Records the report in <see cref="ProgressReports"/>, <see cref="Progress"/> and <see cref="ProgressMessage"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="percent"/> is below 0 or above 100.</exception>
    public Task ReportProgressAsync(int? percent = null, string? message = null, CancellationToken cancellationToken = default)
    {
        HandlerProgress.ThrowIfInvalid(percent);
        lock (_reports)
        {
            _reports.Add((percent, message));
            Progress = percent ?? Progress;
            ProgressMessage = message ?? ProgressMessage;
        }

        return Task.CompletedTask;
    }

    /// <summary>Records <paramref name="reason"/> in <see cref="DeadLetterReason"/>.</summary>
    public void DeadLetter(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        DeadLetterReason = reason;
    }

    /// <summary>Sets <see cref="RetryRequested"/> and records <paramref name="delay"/> in <see cref="RetryDelay"/>.</summary>
    public void Retry(TimeSpan? delay = null)
    {
        RetryRequested = true;
        RetryDelay = delay;
    }
}
