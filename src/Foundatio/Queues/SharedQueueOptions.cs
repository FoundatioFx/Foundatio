using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Foundatio.Queues;

public class SharedQueueOptions<T> : SharedOptions where T : class
{
    public string Name { get; set; } = typeof(T).Name;
    public int Retries { get; set; } = 2;
    public TimeSpan WorkItemTimeout { get; set; } = TimeSpan.FromMinutes(5);
    [DisallowNull]
    public ICollection<IQueueBehavior<T>> Behaviors { get => field; set => field = value ?? new List<IQueueBehavior<T>>(); } = new List<IQueueBehavior<T>>();

    /// <summary>
    /// Allows you to set a prefix on queue metrics. This allows you to have unique metrics for keyed queues (e.g., priority queues).
    /// </summary>
    public string? MetricsPrefix { get; set; }

    /// <summary>
    /// How often to poll queue metrics. These metrics are more expensive to calculate. Defaults to 5 seconds.
    /// </summary>
    public TimeSpan MetricsPollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// If metrics that require polling are enabled. These metrics are more expensive to calculate and should be disabled if you are not using them. Defaults to true.
    /// </summary>
    public bool MetricsPollingEnabled { get; set; } = true;

    /// <summary>
    /// Derives the group or tenant key (see <see cref="QueueEntryOptions.GroupId"/>) from the message payload
    /// when the caller does not specify one. Return <c>null</c> or an empty string to enqueue without a group.
    /// </summary>
    public Func<T, string?>? GroupIdResolver { get; set; }
}

public class SharedQueueOptionsBuilder<T, TOptions, TBuilder> : SharedOptionsBuilder<TOptions, TBuilder>
    where T : class
    where TOptions : SharedQueueOptions<T>, new()
    where TBuilder : SharedQueueOptionsBuilder<T, TOptions, TBuilder>, new()
{
    public TBuilder Name(string? name)
    {
        if (!String.IsNullOrWhiteSpace(name))
            Target.Name = name.Trim();

        return (TBuilder)this;
    }

    public TBuilder Retries(int retries)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retries);

        Target.Retries = retries;
        return (TBuilder)this;
    }

    public TBuilder WorkItemTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);

        Target.WorkItemTimeout = timeout;
        return (TBuilder)this;
    }

    public TBuilder Behaviors(params IQueueBehavior<T>[] behaviors)
    {
        ArgumentNullException.ThrowIfNull(behaviors);

        for (int index = 0; index < behaviors.Length; index++)
            ArgumentNullException.ThrowIfNull(behaviors[index], $"behaviors[{index}]");

        Target.Behaviors = behaviors;
        return (TBuilder)this;
    }

    public TBuilder AddBehavior(IQueueBehavior<T> behavior)
    {
        ArgumentNullException.ThrowIfNull(behavior);

        Target.Behaviors.Add(behavior);
        return (TBuilder)this;
    }

    /// <summary>
    /// Allows you to set a prefix on queue metrics. This allows you to have unique metrics for keyed queues (e.g., priority queues).
    /// </summary>
    public TBuilder MetricsPrefix(string? prefix)
    {
        if (!String.IsNullOrWhiteSpace(prefix))
            Target.MetricsPrefix = prefix.Trim();

        return (TBuilder)this;
    }

    /// <summary>
    /// How often to poll queue metrics. These metrics are more expensive to calculate. Defaults to 5 seconds.
    /// </summary>
    public TBuilder MetricsPollingInterval(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.Zero);

        Target.MetricsPollingInterval = interval;
        return (TBuilder)this;
    }

    /// <summary>
    /// If metrics that require polling are enabled. These metrics are more expensive to calculate and should be disabled if you are not using them. Defaults to true.
    /// </summary>
    public TBuilder MetricsPollingEnabled(bool enabled)
    {
        Target.MetricsPollingEnabled = enabled;
        return (TBuilder)this;
    }

    /// <summary>
    /// Disable metrics collection for this queue.
    /// </summary>
    public TBuilder DisableMetricsPolling()
    {
        Target.MetricsPollingEnabled = false;
        return (TBuilder)this;
    }

    /// <summary>
    /// Derives the group or tenant key from the message payload when the caller does not specify
    /// <see cref="QueueEntryOptions.GroupId"/>. A non-empty per-call value always wins.
    /// </summary>
    public TBuilder GroupId(Func<T, string?> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        Target.GroupIdResolver = resolver;
        return (TBuilder)this;
    }
}
