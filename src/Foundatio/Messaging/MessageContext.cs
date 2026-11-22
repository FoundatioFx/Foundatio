using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Monitoring;
using Foundatio.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging;

internal sealed class MessageContext : TrackedWork, IMessageContext
{
    private static readonly AsyncLocal<MessageContext?> CurrentContext = new();

    private long _progressEmittedAt;
    private IServiceProvider _services = Testing.EmptyServiceProvider.Instance;
    private IServiceScopeFactory? _scopeFactory;
    private AsyncServiceScope? _scope;
    private IReadOnlyDictionary<string, string>? _sentAtHeaders;
    private DateTimeOffset? _sentAt;

    public required string MessageId { get; init; }
    public required string MessageType { get; init; }
    public string? CorrelationId { get; init; }
    public int Attempt { get; init; }
    /// <summary>Parsed from the <see cref="KnownHeaders.SentAt"/> header on first read.</summary>
    public DateTimeOffset? SentAt
    {
        get
        {
            if (_sentAtHeaders is { } headers)
            {
                _sentAt = headers.TryGetValue(KnownHeaders.SentAt, out string? value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? time : null;
                _sentAtHeaders = null;
            }

            return _sentAt;
        }
        init => _sentAt = value;
    }
    public string? GroupKey { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
    public required string Source { get; init; }
    /// <summary>The handler's scope: set for a handler resolved from it, or created on first read (see <see cref="UseScopeOnDemand"/>).</summary>
    public IServiceProvider Services
    {
        get
        {
            if (Volatile.Read(ref _scopeFactory) is { } factory)
                CreateScope(factory);

            return _services;
        }
        set
        {
            _services = value;
            _scopeFactory = null;
        }
    }

    /// <summary>Reads <see cref="SentAt"/> from <paramref name="headers"/> when it is first read, not before.</summary>
    public void ParseSentAtOnDemand(IReadOnlyDictionary<string, string> headers) => _sentAtHeaders = headers;

    /// <summary>
    /// Lets <see cref="Services"/> create a scope from <paramref name="scopeFactory"/> when it is first read, for a handler
    /// that is not resolved from one; <see cref="DisposeScopeAsync"/> then disposes it.
    /// </summary>
    public void UseScopeOnDemand(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
        _services = Testing.EmptyServiceProvider.Instance;
    }

    private void CreateScope(IServiceScopeFactory factory)
    {
        // Handlers rarely read Services from several threads at once; the factory is shared, but only first reads take it.
        lock (factory)
        {
            if (_scopeFactory is null)
                return;

            _scope = factory.CreateAsyncScope();
            _services = _scope.Value.ServiceProvider;
            Volatile.Write(ref _scopeFactory, null);
        }
    }

    /// <summary>Disposes a scope <see cref="Services"/> created on demand, if it did.</summary>
    public ValueTask DisposeScopeAsync()
    {
        _scopeFactory = null;
        if (_scope is not { } scope)
            return ValueTask.CompletedTask;

        _scope = null;
        return scope.DisposeAsync();
    }

    /// <summary>The handler's span, which progress reports add events to.</summary>
    public Activity? Activity { get; init; }

    /// <summary>Logs progress reports at Debug.</summary>
    public ILogger? Logger { get; init; }

    public string? DeadLetterReason { get; private set; }
    public bool RetryRequested { get; private set; }
    public TimeSpan? RetryDelay { get; private set; }

    /// <summary>The message being handled on this async flow, used to propagate the correlation id.</summary>
    public static MessageContext? Current
    {
        get => CurrentContext.Value;
        set => CurrentContext.Value = value;
    }

    public Task ReportProgressAsync(int? percent = null, string? message = null, CancellationToken cancellationToken = default)
    {
        HandlerProgress.ThrowIfInvalid(percent);
        UpdateProgress(percent, message);
        if (ShouldEmit(percent))
            Emit();

        return Task.CompletedTask;
    }

    internal override string TrackedId => MessageId;
    internal override int TrackedAttempt => Attempt;
    internal override string? TrackedCorrelationId => CorrelationId;

    public void DeadLetter(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        DeadLetterReason = reason;
    }

    public void Retry(TimeSpan? delay = null)
    {
        if (delay is { } value)
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero, nameof(delay));

        RetryRequested = true;
        RetryDelay = delay;
    }

    /// <summary>Lets a report through at most every <see cref="HandlerProgress.Interval"/>, and always at 100 percent.</summary>
    private bool ShouldEmit(int? percent)
    {
        long now = Stopwatch.GetTimestamp();
        long last = Volatile.Read(ref _progressEmittedAt);
        if (percent != 100 && last != 0 && Stopwatch.GetElapsedTime(last, now) < HandlerProgress.Interval)
            return false;

        return Interlocked.CompareExchange(ref _progressEmittedAt, now, last) == last || percent == 100;
    }

    private void Emit()
    {
        int? percent = Progress;
        string? message = ProgressMessage;
        Activity?.AddEvent(new ActivityEvent("progress", tags: new ActivityTagsCollection
        {
            ["progress.percent"] = percent,
            ["progress.message"] = message
        }));

        Logger?.LogDebug("Message {MessageId} progress: {Percent}% {ProgressMessage}", MessageId, percent, message);
    }
}
