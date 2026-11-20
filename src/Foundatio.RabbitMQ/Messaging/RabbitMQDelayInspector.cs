using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Warns, from the management API, about what holds up or breaks delayed delivery. The broker's dead-letter worker for
/// a queue forwards at most <c>dead_letter_worker_consumer_prefetch</c> (32) of its expired messages at a time and
/// waits for the next hop to confirm each one, and it tries a message the next hop refused again only after
/// <c>dead_letter_worker_publisher_confirm_timeout</c> (3 minutes by default). The delay levels deliver to a landing
/// queue per target, so enough messages for a target that refuses them hold up that target's delayed messages; a
/// target without a landing queue, such as an instance subscription's queue, holds up a level for every queue under
/// the prefix. The inspector warns about queues that refuse messages when they are full, about policies that change
/// how a delay level, a landing queue or the discard queue behaves, and about a level or landing queue whose expired
/// messages have waited for their next hop too long. Each warning is logged once, until what it reports changes.
/// </summary>
internal sealed class RabbitMQDelayInspector
{
    /// <summary>The broker's default <c>dead_letter_worker_consumer_prefetch</c>: a queue with this many expired messages waiting forwards no others.</summary>
    internal const int DeadLetterWorkerPrefetch = 32;

    /// <summary>How many landing queues a check inspects, those with the most expired messages waiting.</summary>
    private const int LandingQueuesPerCheck = 100;

    /// <summary>The most queues the management API lists on a page.</summary>
    private const int PageSize = 500;

    /// <summary>The fields of a queue the inspector reads.</summary>
    private const string Columns = "name,type,arguments,effective_policy_definition,policy,operator_policy,messages_dlx";

    /// <summary>
    /// The management API lists a queue's policy only from its first statistics, up to <c>collect_statistics_interval</c>
    /// (5 seconds by default) after it was declared, so an inspection right after a declare tries again this often.
    /// </summary>
    private static readonly TimeSpan DetailsRetryDelay = TimeSpan.FromSeconds(5);
    private const int DetailsAttempts = 6;

    private readonly RabbitMQManagementClient _management;
    private readonly RabbitMQMessageTransportNames _names;
    private readonly string _prefix;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _checkInterval;
    private readonly TimeSpan _stallWarningAfter;
    private readonly ConcurrentDictionary<string, string> _reported = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly Dictionary<string, Stall> _stalls = new(StringComparer.Ordinal);
    private long? _lastCheck;
    private bool _checking;

    public RabbitMQDelayInspector(RabbitMQManagementClient management, RabbitMQMessageTransportNames names, string prefix, ILogger logger, TimeProvider timeProvider,
        TimeSpan checkInterval, TimeSpan stallWarningAfter)
    {
        _management = management;
        _names = names;
        _prefix = prefix;
        _logger = logger;
        _timeProvider = timeProvider;
        _checkInterval = checkInterval;
        _stallWarningAfter = stallWarningAfter;
    }

    /// <summary>
    /// Warns when <paramref name="queue"/>, which delayed messages and retries are delivered to, refuses messages once
    /// it is full. <paramref name="isInstance"/> is set for an instance subscription's queue, which has no landing queue.
    /// </summary>
    public void InspectTarget(string queue, JsonElement details, bool isInstance = false)
    {
        if (HasPolicyDetails(details))
            Report(queue, DescribeTarget(details, _prefix, isInstance));
    }

    /// <summary>Reads <paramref name="queue"/>'s details and inspects it as a target, without waiting for it.</summary>
    public void InspectTargetInBackground(string queue, bool isInstance = false)
    {
        Observe(InspectAsync(queue, details => DescribeTarget(details, _prefix, isInstance)));
    }

    /// <summary>Reads the details of the landing queue <paramref name="landing"/> and warns about a policy that changes it, without waiting for it.</summary>
    public void InspectLandingInBackground(string landing)
    {
        Observe(InspectAsync(landing, DescribeLanding));
    }

    /// <summary>
    /// Inspects the delay levels, the landing queues and the discard queue without waiting for it, unless they were
    /// inspected within the check interval (or <paramref name="force"/> is set) or are being inspected now.
    /// </summary>
    public void CheckDelaysInBackground(bool force = false)
    {
        lock (_lock)
        {
            if (_checking || !force && _lastCheck is { } last && _timeProvider.GetElapsedTime(last) < _checkInterval)
                return;

            _checking = true;
            _lastCheck = _timeProvider.GetTimestamp();
        }

        Observe(CheckDelaysAsync(force ? DetailsAttempts : 1));
    }

    /// <summary>
    /// Inspects the delay levels, the landing queues and the discard queue, and how long the expired messages of each
    /// level and landing queue have waited for their next hop, up to <paramref name="attempts"/> times while the
    /// management API does not list the levels' policies yet.
    /// </summary>
    internal async Task CheckDelaysAsync(int attempts)
    {
        try
        {
            for (int attempt = 1; !await CheckDelaysOnceAsync().AnyContext() && attempt < attempts; attempt++)
                await Task.Delay(DetailsRetryDelay, _timeProvider).AnyContext();
        }
        finally
        {
            lock (_lock)
                _checking = false;
        }
    }

    /// <summary>Whether a queue's details include its policy, which the management API lists from its first statistics on.</summary>
    internal static bool HasPolicyDetails(JsonElement queue) => queue.TryGetProperty("effective_policy_definition", out _);

    /// <summary>
    /// Inspects <paramref name="landingQueues"/>, the landing queues with the most expired messages waiting: their
    /// policies, and how long their expired messages have waited for their targets. Those not listed have fewer
    /// waiting than every listed one.
    /// </summary>
    internal void InspectLandingQueues(IEnumerable<JsonElement> landingQueues)
    {
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var queue in landingQueues)
        {
            if (Text(queue, "name") is not { } name)
                continue;

            listed.Add(name);
            if (HasPolicyDetails(queue))
                Report(name, DescribeLanding(queue));
            TrackWaiting(name, isLanding: true, Number(queue, "messages_dlx") ?? 0);
        }

        lock (_lock)
        {
            foreach (var (name, stall) in _stalls)
            {
                if (stall.IsLanding && !listed.Contains(name))
                    _stalls.Remove(name);
            }
        }
    }

    /// <summary>
    /// Lists the delay levels, the discard queue and the landing queues on one page ordered by name, which puts the
    /// levels and the discard queue first, and picks the landing queues with the most expired messages waiting from
    /// it. When they do not all fit, it has the broker order them by their waiting messages instead, which costs it a
    /// read of every queue's statistics in the virtual host.
    /// </summary>
    /// <returns>Whether every delay level and the discard queue were listed with their policies.</returns>
    private async Task<bool> CheckDelaysOnceAsync()
    {
        string prefix = _prefix.Replace(".", "\\.", StringComparison.Ordinal);
        string landingPrefix = $"{_prefix}.landing.";
        using var page = await _management.GetQueuesAsync($"^{prefix}\\.(delay\\.[0-9][0-9]$|discard$|landing\\.)", PageSize, "name", descending: false, Columns,
            CancellationToken.None).AnyContext();
        var items = page.RootElement.GetProperty("items");
        if (Number(page.RootElement, "filtered_count") is { } matching && matching > items.GetArrayLength())
        {
            using var landing = await _management.GetQueuesAsync($"^{prefix}\\.landing\\.", LandingQueuesPerCheck, "messages_dlx", descending: true, columns: null,
                CancellationToken.None).AnyContext();
            InspectLandingQueues(landing.RootElement.GetProperty("items").EnumerateArray());
        }
        else
        {
            InspectLandingQueues(MostWaiting(items.EnumerateArray().Where(IsLanding), LandingQueuesPerCheck));
        }

        int inspected = 0;
        foreach (var queue in items.EnumerateArray())
        {
            string? name = Text(queue, "name");
            if (name is null || IsLanding(queue) || !HasPolicyDetails(queue))
                continue;

            if (name == _names.Discard)
            {
                Report(name, DescribeDiscard(queue));
                inspected++;
            }
            else if (Int32.TryParse(name.AsSpan(name.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out int level)
                     && level < RabbitMQDelayLevels.Count && name == _names.DelayLevel(level))
            {
                Report(name, DescribeLevel(queue, level));
                TrackWaiting(name, isLanding: false, Number(queue, "messages_dlx") ?? 0);
                inspected++;
            }
        }

        return inspected == RabbitMQDelayLevels.Count + 1;

        bool IsLanding(JsonElement queue) => Text(queue, "name")?.StartsWith(landingPrefix, StringComparison.Ordinal) == true;
    }

    /// <summary>
    /// What makes a queue that delayed messages are delivered to hold them up, or null. A queue or subscription holds up
    /// only its own landing queue; an instance subscription's queue, which has none, a delay level.
    /// </summary>
    internal static string? DescribeTarget(JsonElement queue, string prefix, bool isInstance)
    {
        if (!Refuses(queue))
            return null;

        string refuses = $"refuses messages once it holds {Limit(queue)} (overflow {Overflow(queue)}{From(queue)}).";
        if (isInstance)
        {
            return $"{refuses} A delayed retry it refuses holds up its delay level for every queue under the prefix \"{prefix}\": an instance subscription has " +
                $"no landing queue, the broker forwards at most {DeadLetterWorkerPrefetch} of a level's expired messages at a time, and it tries a refused one again " +
                "only after dead_letter_worker_publisher_confirm_timeout (3 minutes by default). Lift the limit for this queue.";
        }

        return $"{refuses} Delayed messages and retries it refuses wait in its landing queue, and once {DeadLetterWorkerPrefetch} wait there, so do its other " +
            "delayed messages, and a subscription's topic's delayed publishes, until the broker tries them again after dead_letter_worker_publisher_confirm_timeout " +
            "(3 minutes by default). Lift the limit for this queue, or keep delayed messages away from it.";
    }

    /// <summary>What a policy changes about a landing queue, or null.</summary>
    internal static string? DescribeLanding(JsonElement queue)
    {
        var problems = new List<string>();
        if (Limit(queue) is { } limit)
            problems.Add($"refuses messages once it holds {limit}, so the delay level that delivers to it stops, for every queue under the prefix");
        if (Overflow(queue) != "reject-publish")
            problems.Add($"dead-letters at most once, because its overflow is {Overflow(queue)}, so a delayed message can be lost when the broker fails while moving it");
        if (Lowest(queue, "expires", "x-expires") is { } expires)
            problems.Add($"is deleted after {expires} ms without a consumer, which a landing queue never has, with any delayed messages its target refused");

        if (problems.Count == 0)
            return null;

        return $"{String.Join("; it ", problems)}{(PolicyNames(queue) is { } policies ? $" ({policies})" : String.Empty)}. Leave the landing queues out of the policy.";
    }

    /// <summary>What makes the discard queue refuse messages, or null.</summary>
    internal static string? DescribeDiscard(JsonElement queue)
    {
        if (!Refuses(queue))
            return null;

        return $"keeps one message and refuses every one after it (overflow {Overflow(queue)}{From(queue)}, with a limit of {Limit(queue)}). Delayed messages for " +
            "deleted queues and for topics without subscriptions then hold up their delay levels, and publishes to topics without subscriptions fail. Leave it out of the policy.";
    }

    /// <summary>What a policy changes about delay level <paramref name="level"/>'s queue, or null.</summary>
    internal static string? DescribeLevel(JsonElement queue, int level)
    {
        var problems = new List<string>();
        if (Limit(queue) is { } limit)
            problems.Add($"refuses messages once it holds {limit}, so delayed sends fail and the level above it stops");
        if (Overflow(queue) != "reject-publish")
            problems.Add($"dead-letters at most once, because its overflow is {Overflow(queue)}, so a delayed message can be lost when the broker fails while moving it");
        long levelTtl = RabbitMQDelayLevels.TimeToLiveMilliseconds(level);
        if (Lowest(queue, "message-ttl", "x-message-ttl") is { } ttl && ttl < levelTtl)
            problems.Add($"expires messages after {ttl} ms instead of {levelTtl} ms, so delays through it end early");
        if (Lowest(queue, "expires", "x-expires") is { } expires)
            problems.Add($"is deleted, with its delayed messages, after {expires} ms without a consumer, which a delay level never has");

        if (problems.Count == 0)
            return null;

        return $"{String.Join("; it ", problems)}{(PolicyNames(queue) is { } policies ? $" ({policies})" : String.Empty)}. Leave the delay levels out of the policy.";
    }

    /// <summary>The <paramref name="count"/> queues with the most expired messages waiting, by name among those with as many.</summary>
    private static IEnumerable<JsonElement> MostWaiting(IEnumerable<JsonElement> queues, int count)
    {
        return queues.OrderByDescending(queue => Number(queue, "messages_dlx") ?? 0).ThenBy(queue => Text(queue, "name"), StringComparer.Ordinal).Take(count);
    }

    private async Task InspectAsync(string queue, Func<JsonElement, string?> describe)
    {
        for (int attempt = 1; attempt <= DetailsAttempts; attempt++)
        {
            if (attempt > 1)
                await Task.Delay(DetailsRetryDelay, _timeProvider).AnyContext();

            using var details = await _management.GetQueueAsync(queue, CancellationToken.None).AnyContext();
            if (details is null)
                return;

            if (HasPolicyDetails(details.RootElement))
            {
                Report(queue, describe(details.RootElement));
                return;
            }
        }
    }

    private void TrackWaiting(string queue, bool isLanding, long waiting)
    {
        long now = _timeProvider.GetTimestamp();
        lock (_lock)
        {
            if (waiting < DeadLetterWorkerPrefetch)
            {
                if (_stalls.Remove(queue, out var caughtUp) && caughtUp.Reported)
                {
                    if (isLanding)
                        _logger.LogInformation("Landing queue {Queue} caught up: {Count} expired messages wait for their target", queue, waiting);
                    else
                        _logger.LogInformation("Delay level {Queue} caught up: {Count} expired messages wait for their next hop", queue, waiting);
                }

                return;
            }

            var stall = _stalls.TryGetValue(queue, out var known) ? known : new Stall(now, isLanding, Reported: false);
            var waited = _timeProvider.GetElapsedTime(stall.Since, now);
            if (stall.Reported || waited < _stallWarningAfter)
            {
                _stalls[queue] = stall;
                return;
            }

            _stalls[queue] = stall with { Reported = true };
            if (isLanding)
            {
                _logger.LogWarning("Landing queue {Queue} has had at least {Threshold} expired messages waiting for {Waited} ({Count} now), so delayed messages for {Target} " +
                    "are late. It refuses them, for example because it is full under a policy that sets overflow reject-publish, or is a quorum queue without a majority, and the " +
                    "broker tries them again after dead_letter_worker_publisher_confirm_timeout (3 minutes by default). Other queues' delayed messages are not held up",
                    queue, DeadLetterWorkerPrefetch, waited, waiting, _names.LandingTarget(queue) ?? "its target");
            }
            else
            {
                _logger.LogWarning("Delay level {Queue} has had at least {Threshold} expired messages waiting for their next hop for {Waited} ({Count} now), so delayed messages " +
                    "through it are late, for every queue under its prefix. A queue the level delivers to holds it up while it refuses messages, until the broker tries them " +
                    "again (dead_letter_worker_publisher_confirm_timeout, 3 minutes by default): an instance subscription's queue, or a queue whose landing queue was deleted, " +
                    "that is full under a policy that sets overflow reject-publish; a delay level, landing queue or the discard queue under a policy that limits its length; " +
                    "or a quorum queue without a majority", queue, DeadLetterWorkerPrefetch, waited, waiting);
            }
        }
    }

    private void Report(string queue, string? problem)
    {
        if (problem is null)
        {
            _reported.TryRemove(queue, out _);
            return;
        }

        if (_reported.TryGetValue(queue, out string? reported) && reported == problem)
            return;

        _reported[queue] = problem;
        _logger.LogWarning("RabbitMQ queue {Queue} {Problem}", queue, problem);
    }

    private void Observe(Task task)
    {
        _ = task.ContinueWith(static (t, state) => ((ILogger)state!).LogDebug(t.Exception!.GetBaseException(), "Could not inspect delayed delivery through the RabbitMQ management API"),
            _logger, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Whether the queue refuses publishes when it is full. A quorum queue takes its policy's overflow over its
    /// argument's and treats reject-publish-dlx as drop-head; a classic queue takes its argument's.
    /// </summary>
    private static bool Refuses(JsonElement queue) => Overflow(queue).StartsWith("reject-publish", StringComparison.Ordinal) && Limit(queue) is not null;

    private static string Overflow(JsonElement queue)
    {
        string? policy = PolicySetting(queue, "overflow") is { ValueKind: JsonValueKind.String } p ? p.GetString() : null;
        string? argument = Argument(queue, "x-overflow") is { ValueKind: JsonValueKind.String } a ? a.GetString() : null;
        if (Text(queue, "type") == "quorum")
            return (policy ?? argument) == "reject-publish" ? "reject-publish" : "drop-head";

        return argument ?? policy ?? "drop-head";
    }

    private static string? Limit(JsonElement queue)
    {
        long? messages = Lowest(queue, "max-length", "x-max-length"), bytes = Lowest(queue, "max-length-bytes", "x-max-length-bytes");
        return (messages, bytes) switch
        {
            ({ } m, { } b) => $"{Messages(m)} or {b} bytes",
            ({ } m, null) => Messages(m),
            (null, { } b) => $"{b} bytes",
            _ => null
        };
    }

    private static string Messages(long count) => count == 1 ? "1 message" : $"{count} messages";

    /// <summary>The lower of a setting's policy value and argument value, which is the one RabbitMQ applies.</summary>
    private static long? Lowest(JsonElement queue, string policyKey, string argumentKey)
    {
        long? policy = PolicySetting(queue, policyKey) is { ValueKind: JsonValueKind.Number } p && p.TryGetInt64(out long pv) ? pv : null;
        long? argument = Argument(queue, argumentKey) is { ValueKind: JsonValueKind.Number } a && a.TryGetInt64(out long av) ? av : null;
        return policy is { } x && argument is { } y ? Math.Min(x, y) : policy ?? argument;
    }

    private static string From(JsonElement queue) => PolicyNames(queue) is { } policies ? $", from {policies}" : String.Empty;

    private static string? PolicyNames(JsonElement queue)
    {
        string? policy = Text(queue, "policy"), operatorPolicy = Text(queue, "operator_policy");
        return (policy, operatorPolicy) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"policy {policy} and operator policy {operatorPolicy}",
            ({ Length: > 0 }, _) => $"policy {policy}",
            (_, { Length: > 0 }) => $"operator policy {operatorPolicy}",
            _ => null
        };
    }

    /// <summary>A value from the queue's effective policy definition, which RabbitMQ lists as an empty array when there is none.</summary>
    private static JsonElement? PolicySetting(JsonElement queue, string key) => Property(queue, "effective_policy_definition", key);

    private static JsonElement? Argument(JsonElement queue, string key) => Property(queue, "arguments", key);

    private static JsonElement? Property(JsonElement queue, string section, string key)
    {
        return queue.TryGetProperty(section, out var values) && values.ValueKind == JsonValueKind.Object && values.TryGetProperty(key, out var value) ? value : null;
    }

    private static string? Text(JsonElement queue, string property)
    {
        return queue.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static long? Number(JsonElement queue, string property)
    {
        return queue.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : null;
    }

    /// <summary>Since when a queue has had at least <see cref="DeadLetterWorkerPrefetch"/> expired messages waiting, and whether that was reported.</summary>
    private readonly record struct Stall(long Since, bool IsLanding, bool Reported);
}
