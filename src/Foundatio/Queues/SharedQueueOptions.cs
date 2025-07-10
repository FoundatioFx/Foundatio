using System;
using System.Collections.Generic;

namespace Foundatio.Queues;

public class SharedQueueOptions<T> : SharedOptions where T : class
{
    public string Name { get; set; } = typeof(T).Name;
    public int Retries { get; set; } = 2;
    public TimeSpan WorkItemTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public ICollection<IQueueBehavior<T>> Behaviors { get; set; } = new List<IQueueBehavior<T>>();

    /// <summary>
    /// Allows you to set a prefix on queue metrics. This allows you to have unique metrics for keyed queues (e.g., priority queues).
    /// </summary>
    public string MetricsPrefix { get; set; }

    /// <summary>
    /// How often to poll queue metrics. These metrics are more expensive to calculate. Defaults to 5 seconds.
    /// </summary>
    public TimeSpan MetricsPollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// If metrics that require polling are enabled. These metrics are more expensive to calculate and should be disabled if you are not using them. Defaults to true.
    /// </summary>
    public bool MetricsPollingEnabled { get; set; } = true;
}

public class SharedQueueOptionsBuilder<T, TOptions, TBuilder> : SharedOptionsBuilder<TOptions, TBuilder>
    where T : class
    where TOptions : SharedQueueOptions<T>, new()
    where TBuilder : SharedQueueOptionsBuilder<T, TOptions, TBuilder>, new()
{
    public TBuilder Name(string name)
    {
        if (!String.IsNullOrWhiteSpace(name))
            Target.Name = name.Trim();

        return (TBuilder)this;
    }

    public TBuilder Retries(int retries)
    {
        if (retries < 0)
            throw new ArgumentOutOfRangeException(nameof(retries));

        Target.Retries = retries;
        return (TBuilder)this;
    }

    public TBuilder WorkItemTimeout(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        Target.WorkItemTimeout = timeout;
        return (TBuilder)this;
    }

    public TBuilder Behaviors(params IQueueBehavior<T>[] behaviors)
    {
        Target.Behaviors = behaviors;
        return (TBuilder)this;
    }

    public TBuilder AddBehavior(IQueueBehavior<T> behavior)
    {
        if (behavior == null)
            throw new ArgumentNullException(nameof(behavior));

        if (Target.Behaviors == null)
            Target.Behaviors = new List<IQueueBehavior<T>>();
        Target.Behaviors.Add(behavior);

        return (TBuilder)this;
    }

    /// <summary>
    /// Allows you to set a prefix on queue metrics. This allows you to have unique metrics for keyed queues (e.g., priority queues).
    /// </summary>
    public TBuilder MetricsPrefix(string prefix)
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
        if (interval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));

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
}
