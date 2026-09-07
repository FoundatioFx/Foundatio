using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace Foundatio.Messaging;

/// <summary>
/// Options for configuring <see cref="RedisMessageExecutionStore"/>.
/// </summary>
public class RedisMessageExecutionStoreOptions
{
    /// <summary>
    /// Key prefix for all Redis keys. Default is "fnd:executions".
    /// </summary>
    public string KeyPrefix { get; set; } = "fnd:executions";

    /// <summary>
    /// Optional prefix applied before <see cref="KeyPrefix"/> for app-level scoping.
    /// When set, all Redis keys become <c>"{ResourcePrefix}:{KeyPrefix}:..."</c>.
    /// When <c>null</c> or empty (default), only <see cref="KeyPrefix"/> is used.
    /// </summary>
    /// <remarks>
    /// Use this to isolate multiple applications sharing the same Redis instance
    /// (e.g., <c>"myapp"</c> produces keys like <c>"myapp:fnd:executions:..."</c>).
    /// </remarks>
    public string? ResourcePrefix { get; set; }

    /// <summary>
    /// TTL applied to a job's keys by any write whose caller passes no <c>expiry</c>.
    /// Default is 24 hours. Set to <c>null</c> to disable auto-expiry for such writes.
    /// </summary>
    /// <remarks>
    /// The queue worker passes <c>MessageExecutionOptions.StateRetention</c> on every write, so this only
    /// takes effect for direct callers of the store. Writes that leave a job <see cref="MessageExecutionStatus.Queued"/>
    /// or <see cref="MessageExecutionStatus.Processing"/> are raised to at least <see cref="NonTerminalExpiry"/>.
    /// </remarks>
    public TimeSpan? DefaultExpiry { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Minimum TTL for a job while it is <see cref="MessageExecutionStatus.Queued"/> or <see cref="MessageExecutionStatus.Processing"/>,
    /// so a live job does not vanish before it reaches a terminal state. Default is 7 days. A longer caller-supplied
    /// expiry still wins; a <c>null</c> effective expiry (no TTL) is left as is.
    /// </summary>
    public TimeSpan NonTerminalExpiry { get; set; } = TimeSpan.FromDays(7);
}
