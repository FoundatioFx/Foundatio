using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace Foundatio.Monitoring;

/// <summary>
/// Follows the messages and jobs this process handles, for <see cref="IWorkMonitor"/>: what is running, what finished
/// recently, and rolling counts per message type and source and per job name. It is always on. Following fast work
/// costs a few interlocked updates and two short locks; the lists are only built when a snapshot is taken.
/// </summary>
/// <remarks>
/// Running work is listed once it has run for <see cref="ActiveThreshold"/> or reported progress. Finished work is kept
/// in a ring buffer when it was listed that way, or when it did not succeed. Payloads and headers are never kept, and
/// errors are cut to <see cref="MaxErrorLength"/> characters.
/// </remarks>
internal sealed class WorkTracker
{
    /// <summary>Errors are cut to this many characters.</summary>
    public const int MaxErrorLength = 1024;

    /// <summary>Beyond this many message types, sources and job names, work is counted under <see cref="OtherName"/>.</summary>
    public const int MaxCounters = 2048;

    /// <summary>The name work is counted under once there are <see cref="MaxCounters"/> counters.</summary>
    public const string OtherName = "(other)";

    private readonly TimeProvider _timeProvider;
    private readonly Stripe[] _stripes;
    private readonly ConcurrentDictionary<(WorkKind Kind, string Name, string? Source), WorkCounter> _counters = new();
    private readonly CompletedWorkEntry?[] _recent;
    private readonly int _windowMinutes;
    private readonly long _thresholdTimestamps;
    private int _recentNext;
    private long _version;

    public WorkTracker(TimeProvider timeProvider, TimeSpan activeThreshold, int recentCapacity, TimeSpan statsWindow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(activeThreshold, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(recentCapacity, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(statsWindow, TimeSpan.FromMinutes(1));

        _timeProvider = timeProvider;
        ActiveThreshold = activeThreshold;
        _thresholdTimestamps = (long)(activeThreshold.TotalSeconds * timeProvider.TimestampFrequency);
        _recent = new CompletedWorkEntry?[recentCapacity];
        _windowMinutes = (int)Math.Ceiling(statsWindow.TotalMinutes);
        StatsWindow = TimeSpan.FromMinutes(_windowMinutes);

        int stripes = 1;
        while (stripes < Environment.ProcessorCount && stripes < 16)
            stripes <<= 1;

        _stripes = new Stripe[stripes];
        for (int i = 0; i < stripes; i++)
            _stripes[i] = new Stripe();
    }

    /// <summary>Creates the tracker the service provider shares between messaging, jobs and monitoring.</summary>
    public static WorkTracker Create(IServiceProvider services)
    {
        var options = services.GetService<MonitoringRegistrations>()?.Options ?? new MonitoringOptions();
        return new WorkTracker(services.GetService<TimeProvider>() ?? TimeProvider.System, options.ActiveThreshold, options.RecentCapacity, options.StatsWindow);
    }

    /// <summary>How long work runs before it is listed as active, unless it reports progress sooner.</summary>
    public TimeSpan ActiveThreshold { get; }

    /// <summary>How far back the counts reach, in whole minutes.</summary>
    public TimeSpan StatsWindow { get; }

    /// <summary>Changes whenever listed work changes: work finished and was kept, or progress was reported.</summary>
    public long Version => Interlocked.Read(ref _version);

    public long GetTimestamp() => _timeProvider.GetTimestamp();

    /// <summary>The counter for a message type on a source, or a job name. Callers cache it.</summary>
    public WorkCounter GetCounter(WorkKind kind, string name, string? source)
    {
        var key = (kind, name, source);
        if (_counters.TryGetValue(key, out var counter))
            return counter;

        if (_counters.Count >= MaxCounters)
            key = (kind, OtherName, null);

        return _counters.GetOrAdd(key, static (k, tracker) => new WorkCounter(k.Kind, k.Name, k.Source, tracker._windowMinutes, tracker._timeProvider.TimestampFrequency), this);
    }

    /// <summary>Starts following <paramref name="work"/>, which <paramref name="handler"/> is about to handle.</summary>
    public void Start(TrackedWork work, WorkCounter counter, string? handler)
    {
        work.Tracker = this;
        work.Counter = counter;
        work.Handler = handler;
        work.StartedTimestamp = _timeProvider.GetTimestamp();
        work.Stripe = Environment.CurrentManagedThreadId & (_stripes.Length - 1);
        counter.Started();

        var stripe = _stripes[work.Stripe];
        lock (stripe)
        {
            work.Running = true;
            work.Next = stripe.Head;
            if (stripe.Head is not null)
                stripe.Head.Previous = work;

            stripe.Head = work;
        }
    }

    /// <summary>The handler is done; the work is no longer active, even while its outcome is being saved.</summary>
    public void Stop(TrackedWork work)
    {
        var stripe = _stripes[work.Stripe];
        lock (stripe)
        {
            if (!work.Running)
                return;

            work.Running = false;
            if (work.Previous is null)
                stripe.Head = work.Next;
            else
                work.Previous.Next = work.Next;

            if (work.Next is not null)
                work.Next.Previous = work.Previous;

            work.Previous = work.Next = null;
        }

        work.StoppedTimestamp = _timeProvider.GetTimestamp();
        work.Counter?.Stopped();
    }

    /// <summary>Counts how the work ended, and keeps it in the recent list when it was visible or did not succeed.</summary>
    public void Complete(TrackedWork work, WorkOutcome outcome, string? error)
    {
        if (work.Counter is not { } counter)
            return;

        Stop(work);
        long now = _timeProvider.GetTimestamp();
        var duration = _timeProvider.GetElapsedTime(work.StartedTimestamp, work.StoppedTimestamp);
        counter.Record(now, outcome, duration);

        if (!IsFailure(outcome) && duration < ActiveThreshold && work.ProgressTimestamp == 0)
            return;

        var completedAt = _timeProvider.GetUtcNow() - _timeProvider.GetElapsedTime(work.StoppedTimestamp, now);
        AddRecent(new CompletedWorkEntry
        {
            Kind = counter.Kind,
            Id = work.TrackedId,
            Name = counter.Name,
            Source = counter.Source,
            Handler = work.Handler,
            Attempt = work.TrackedAttempt,
            CorrelationId = work.TrackedCorrelationId,
            StartedAt = completedAt - duration,
            CompletedAt = completedAt,
            Duration = duration,
            Outcome = outcome,
            Error = Truncate(error),
            Progress = work.Progress,
            ProgressMessage = work.ProgressMessage
        });
    }

    /// <summary>Counts work that ended without a handler running, such as a skipped message or a job that could not start.</summary>
    public void Record(WorkCounter counter, WorkOutcome outcome, string id, int attempt, string? correlationId, string? error)
    {
        counter.Record(_timeProvider.GetTimestamp(), outcome, null);
        if (!IsFailure(outcome))
            return;

        var now = _timeProvider.GetUtcNow();
        AddRecent(new CompletedWorkEntry
        {
            Kind = counter.Kind,
            Id = id,
            Name = counter.Name,
            Source = counter.Source,
            Attempt = attempt,
            CorrelationId = correlationId,
            StartedAt = now,
            CompletedAt = now,
            Outcome = outcome,
            Error = Truncate(error)
        });
    }

    public void OnProgress() => Interlocked.Increment(ref _version);

    /// <summary>
    /// Whether listed work changed since <paramref name="version"/>, or work became active (passed the threshold)
    /// after <paramref name="timestamp"/>.
    /// </summary>
    public bool HasChangedSince(long version, long timestamp)
    {
        if (Version != version)
            return true;

        long now = _timeProvider.GetTimestamp();
        foreach (var stripe in _stripes)
        {
            lock (stripe)
            {
                for (var work = stripe.Head; work is not null; work = work.Next)
                {
                    long visibleAt = work.StartedTimestamp + _thresholdTimestamps;
                    if (visibleAt > timestamp && visibleAt <= now)
                        return true;
                }
            }
        }

        return false;
    }

    public WorkSnapshot GetSnapshot()
    {
        long now = _timeProvider.GetTimestamp();
        var utcNow = _timeProvider.GetUtcNow();

        var active = new List<ActiveWorkEntry>();
        foreach (var stripe in _stripes)
        {
            lock (stripe)
            {
                for (var work = stripe.Head; work is not null; work = work.Next)
                {
                    var elapsed = _timeProvider.GetElapsedTime(work.StartedTimestamp, now);
                    long progressAt = work.ProgressTimestamp;
                    if (elapsed < ActiveThreshold && progressAt == 0 || work.Counter is not { } counter)
                        continue;

                    active.Add(new ActiveWorkEntry
                    {
                        Kind = counter.Kind,
                        Id = work.TrackedId,
                        Name = counter.Name,
                        Source = counter.Source,
                        Handler = work.Handler,
                        Attempt = work.TrackedAttempt,
                        CorrelationId = work.TrackedCorrelationId,
                        StartedAt = utcNow - elapsed,
                        Progress = work.Progress,
                        ProgressMessage = work.ProgressMessage,
                        ProgressAt = progressAt == 0 ? null : utcNow - _timeProvider.GetElapsedTime(progressAt, now)
                    });
                }
            }
        }

        active.Sort(static (a, b) => a.StartedAt.CompareTo(b.StartedAt));

        var stats = new List<WorkStatsEntry>();
        foreach (var counter in _counters.Values)
        {
            if (counter.GetStats(now) is { } entry)
                stats.Add(entry);
        }

        return new WorkSnapshot { Active = active, Recent = GetRecent(), Stats = stats, StatsWindow = StatsWindow };
    }

    /// <summary>Cuts <paramref name="error"/> to <see cref="MaxErrorLength"/> characters, without splitting a surrogate pair.</summary>
    public static string? Truncate(string? error)
    {
        if (error is null || error.Length <= MaxErrorLength)
            return error;

        int length = MaxErrorLength - 3;
        if (Char.IsHighSurrogate(error[length - 1]))
            length--;

        return String.Concat(error.AsSpan(0, length), "...");
    }

    private static bool IsFailure(WorkOutcome outcome) => outcome is not (WorkOutcome.Succeeded or WorkOutcome.Skipped);

    private void AddRecent(CompletedWorkEntry entry)
    {
        if (_recent.Length > 0)
        {
            lock (_recent)
            {
                _recent[_recentNext] = entry;
                _recentNext = (_recentNext + 1) % _recent.Length;
            }
        }

        Interlocked.Increment(ref _version);
    }

    private List<CompletedWorkEntry> GetRecent()
    {
        var recent = new List<CompletedWorkEntry>(_recent.Length);
        lock (_recent)
        {
            for (int i = 1; i <= _recent.Length; i++)
            {
                if (_recent[(_recentNext - i + _recent.Length) % _recent.Length] is { } entry)
                    recent.Add(entry);
            }
        }

        return recent;
    }

    private sealed class Stripe
    {
        public TrackedWork? Head;
    }
}
