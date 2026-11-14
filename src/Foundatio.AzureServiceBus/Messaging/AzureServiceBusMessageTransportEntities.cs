using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Foundatio.Extensions;
using Microsoft.Extensions.Logging;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Creates, checks, counts and deletes the queues, topics and subscriptions of <see cref="AzureServiceBusMessageTransport"/>
/// through the administration client. Queues and topics found or created are remembered; subscriptions are checked every
/// time a receiver opens or a message is sent to one, so one deleted by another process is created again.
/// </summary>
/// <remarks>
/// Every subscription has the rule <see cref="RuleName"/>, which matches a message that names no target subscription or
/// names this one, so a message sent to one subscription through its topic reaches only that one. A subscription that
/// already exists with only the default rule, which matches everything, has it replaced by this rule; one with other
/// rules is left alone.
/// </remarks>
internal sealed class AzureServiceBusMessageTransportEntities
{
    public const string RuleName = "foundatio";
    private const string DefaultRuleName = "$Default";

    private readonly ServiceBusAdministrationClient? _admin;
    private readonly AzureServiceBusMessageTransportOptions _options;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<TimeSpan?>>> _queues = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<TimeSpan?>>> _topics = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _checkedRules = new(StringComparer.OrdinalIgnoreCase);

    public AzureServiceBusMessageTransportEntities(ServiceBusAdministrationClient? admin, AzureServiceBusMessageTransportOptions options, ILogger logger)
    {
        _admin = admin;
        _options = options;
        _logger = logger;
    }

    /// <summary>The SQL filter of the rule every subscription gets, matching messages for all subscriptions or for <paramref name="subscription"/>.</summary>
    public static string GetRuleFilter(string subscription) =>
        // NOT EXISTS() would read better, but the Service Bus emulator does not evaluate it for bracketed names.
        $"[{AzureServiceBusMessageTransportCodec.TargetProperty}] IS NULL OR [{AzureServiceBusMessageTransportCodec.TargetProperty}] = '{subscription}'";

    /// <summary>Makes sure the queue exists, and returns its lock duration, or null when entities are managed elsewhere.</summary>
    public Task<TimeSpan?> EnsureQueueAsync(string name, CancellationToken cancellationToken) =>
        _options.CreateEntities ? GetOrCreateAsync(_queues, name, () => ResolveQueueAsync(name), cancellationToken) : Task.FromResult<TimeSpan?>(null);

    public Task EnsureTopicAsync(string name, CancellationToken cancellationToken) =>
        _options.CreateEntities ? GetOrCreateAsync(_topics, name, () => ResolveTopicAsync(name), cancellationToken) : Task.CompletedTask;

    /// <summary>Makes sure the topic and its subscription exist with the transport's rule, and returns the subscription's lock duration.</summary>
    public async Task<TimeSpan?> EnsureSubscriptionAsync(string topic, string subscription, CancellationToken cancellationToken)
    {
        if (!_options.CreateEntities)
            return null;

        await EnsureTopicAsync(topic, cancellationToken).AnyContext();
        var admin = Admin;
        SubscriptionProperties properties;
        try
        {
            properties = (await admin.GetSubscriptionAsync(topic, subscription, cancellationToken).AnyContext()).Value;
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
        {
            try
            {
                properties = await CreateSubscriptionAsync(topic, subscription, autoDeleteOnIdle: null, cancellationToken).AnyContext();
            }
            catch (ServiceBusException createEx) when (createEx.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
            {
                // The topic was deleted since it was remembered.
                ForgetTopic(topic);
                await EnsureTopicAsync(topic, cancellationToken).AnyContext();
                properties = await CreateSubscriptionAsync(topic, subscription, autoDeleteOnIdle: null, cancellationToken).AnyContext();
            }
            catch (ServiceBusException createEx) when (createEx.Reason == ServiceBusFailureReason.MessagingEntityAlreadyExists)
            {
                properties = (await admin.GetSubscriptionAsync(topic, subscription, cancellationToken).AnyContext()).Value;
            }

            _checkedRules.TryAdd($"{topic}/{subscription}", 0);
            return properties.LockDuration;
        }

        ValidateSessions(properties.RequiresSession, $"Subscription {subscription} of topic {topic}");
        string key = $"{topic}/{subscription}";
        if (_checkedRules.TryAdd(key, 0))
        {
            try
            {
                await CheckRulesAsync(topic, subscription, cancellationToken).AnyContext();
            }
            catch
            {
                _checkedRules.TryRemove(key, out _);
                throw;
            }
        }

        return properties.LockDuration;
    }

    /// <summary>Creates a new instance subscription that Service Bus removes once it has been idle for the instance timeout.</summary>
    public async Task<TimeSpan?> CreateInstanceSubscriptionAsync(string topic, string subscription, CancellationToken cancellationToken)
    {
        if (!_options.CreateEntities)
            throw new InvalidOperationException("Instance subscriptions are created for each receiver, which needs CreateEntities. Subscribe with a durable subscription instead, or turn CreateEntities on.");

        await EnsureTopicAsync(topic, cancellationToken).AnyContext();
        try
        {
            return (await CreateSubscriptionAsync(topic, subscription, _options.InstanceSubscriptionIdleTimeout, cancellationToken).AnyContext()).LockDuration;
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
        {
            ForgetTopic(topic);
            await EnsureTopicAsync(topic, cancellationToken).AnyContext();
            return (await CreateSubscriptionAsync(topic, subscription, _options.InstanceSubscriptionIdleTimeout, cancellationToken).AnyContext()).LockDuration;
        }
    }

    public async Task DeleteQueueAsync(string name, CancellationToken cancellationToken)
    {
        var admin = GetAdminForDelete();
        try
        {
            // The emulator answers a delete of a missing queue with Bad Request rather than not found.
            if (await admin.QueueExistsAsync(name, cancellationToken).AnyContext())
            {
                await admin.DeleteQueueAsync(name, cancellationToken).AnyContext();
                _logger.LogDebug("Deleted queue {QueueName}", name);
            }
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
        {
        }
        finally
        {
            ForgetQueue(name);
        }
    }

    public async Task DeleteSubscriptionAsync(string topic, string subscription, CancellationToken cancellationToken)
    {
        var admin = GetAdminForDelete();
        _checkedRules.TryRemove($"{topic}/{subscription}", out _);
        try
        {
            // The emulator answers a delete in a missing topic with Bad Request rather than not found.
            if (await admin.SubscriptionExistsAsync(topic, subscription, cancellationToken).AnyContext())
            {
                await admin.DeleteSubscriptionAsync(topic, subscription, cancellationToken).AnyContext();
                _logger.LogDebug("Deleted subscription {Subscription} of topic {Topic}", subscription, topic);
            }
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
        {
        }
    }

    /// <summary>Deletes an instance subscription without failing, since Service Bus removes it anyway once it is idle.</summary>
    public async Task TryDeleteInstanceSubscriptionAsync(string topic, string subscription)
    {
        if (_admin is null)
            return;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _admin.DeleteSubscriptionAsync(topic, subscription, timeout.Token).AnyContext();
            _logger.LogDebug("Deleted instance subscription {Subscription} of topic {Topic}", subscription, topic);
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete instance subscription {Subscription} of topic {Topic}; Service Bus removes it once it has been idle for {IdleTimeout}",
                subscription, topic, _options.InstanceSubscriptionIdleTimeout);
        }
    }

    /// <summary>The queue's active, scheduled and dead-lettered counts, all 0 when it does not exist.</summary>
    public async Task<(long Active, long Scheduled, long DeadLettered)> GetQueueCountsAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var properties = (await Admin.GetQueueRuntimePropertiesAsync(name, cancellationToken).AnyContext()).Value;
            return (properties.ActiveMessageCount, properties.ScheduledMessageCount, properties.DeadLetterMessageCount);
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
        {
            return (0, 0, 0);
        }
    }

    /// <summary>
    /// The subscription's active and dead-lettered counts, both 0 when it does not exist. Its scheduled messages wait in
    /// the topic, which counts them for all of its subscriptions together.
    /// </summary>
    public async Task<(long Active, long DeadLettered)> GetSubscriptionCountsAsync(string topic, string subscription, CancellationToken cancellationToken)
    {
        try
        {
            var properties = (await Admin.GetSubscriptionRuntimePropertiesAsync(topic, subscription, cancellationToken).AnyContext()).Value;
            return (properties.ActiveMessageCount, properties.DeadLetterMessageCount);
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
        {
            return (0, 0);
        }
    }

    public void ForgetQueue(string name) => _queues.TryRemove(name, out _);

    public void ForgetTopic(string name) => _topics.TryRemove(name, out _);

    private ServiceBusAdministrationClient Admin => _admin
        ?? throw new InvalidOperationException("The transport has no administration client. Pass one to the constructor, or create the transport from a connection string or namespace.");

    private ServiceBusAdministrationClient GetAdminForDelete()
    {
        if (!_options.CreateEntities)
            throw new InvalidOperationException("Queues and subscriptions are only deleted by a transport with CreateEntities; with it off they are managed outside the transport. Dead letters can still be deleted.");

        return Admin;
    }

    private async Task<TimeSpan?> ResolveQueueAsync(string name)
    {
        var admin = Admin;
        QueueProperties properties;
        try
        {
            properties = (await admin.GetQueueAsync(name).AnyContext()).Value;
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
        {
            var options = new CreateQueueOptions(name)
            {
                LockDuration = _options.LockDuration,
                MaxDeliveryCount = _options.MaxDeliveryCount,
                RequiresSession = _options.RequiresSession,
                RequiresDuplicateDetection = _options.RequiresDuplicateDetection
            };
            if (_options.RequiresDuplicateDetection)
                options.DuplicateDetectionHistoryTimeWindow = _options.DuplicateDetectionWindow;
            if (LargeMessageSizeInKilobytes is { } kilobytes)
                options.MaxMessageSizeInKilobytes = kilobytes;

            try
            {
                properties = (await admin.CreateQueueAsync(options).AnyContext()).Value;
                _logger.LogDebug("Created queue {QueueName}", name);
                return properties.LockDuration;
            }
            catch (ServiceBusException createEx) when (createEx.Reason == ServiceBusFailureReason.MessagingEntityAlreadyExists)
            {
                properties = (await admin.GetQueueAsync(name).AnyContext()).Value;
            }
        }

        ValidateSessions(properties.RequiresSession, $"Queue {name}");
        WarnOnDuplicateDetection(properties.RequiresDuplicateDetection, $"Queue {name}");
        return properties.LockDuration;
    }

    private async Task<TimeSpan?> ResolveTopicAsync(string name)
    {
        var admin = Admin;
        TopicProperties properties;
        try
        {
            properties = (await admin.GetTopicAsync(name).AnyContext()).Value;
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
        {
            var options = new CreateTopicOptions(name) { RequiresDuplicateDetection = _options.RequiresDuplicateDetection };
            if (_options.RequiresDuplicateDetection)
                options.DuplicateDetectionHistoryTimeWindow = _options.DuplicateDetectionWindow;
            if (LargeMessageSizeInKilobytes is { } kilobytes)
                options.MaxMessageSizeInKilobytes = kilobytes;

            try
            {
                await admin.CreateTopicAsync(options).AnyContext();
                _logger.LogDebug("Created topic {Topic}", name);
                return null;
            }
            catch (ServiceBusException createEx) when (createEx.Reason == ServiceBusFailureReason.MessagingEntityAlreadyExists)
            {
                properties = (await admin.GetTopicAsync(name).AnyContext()).Value;
            }
        }

        WarnOnDuplicateDetection(properties.RequiresDuplicateDetection, $"Topic {name}");
        return null;
    }

    private async Task<SubscriptionProperties> CreateSubscriptionAsync(string topic, string subscription, TimeSpan? autoDeleteOnIdle, CancellationToken cancellationToken)
    {
        var options = new CreateSubscriptionOptions(topic, subscription)
        {
            LockDuration = _options.LockDuration,
            MaxDeliveryCount = _options.MaxDeliveryCount,
            RequiresSession = _options.RequiresSession
        };
        if (autoDeleteOnIdle is { } idle)
            options.AutoDeleteOnIdle = idle;

        var properties = (await Admin.CreateSubscriptionAsync(options, new CreateRuleOptions(RuleName, new SqlRuleFilter(GetRuleFilter(subscription))), cancellationToken).AnyContext()).Value;
        _logger.LogDebug("Created subscription {Subscription} of topic {Topic}", subscription, topic);
        return properties;
    }

    /// <summary>
    /// Gives an existing subscription the transport's rule when it only has the default rule, which matches every
    /// message, and finishes a replacement that stopped after adding the transport's rule.
    /// </summary>
    private async Task CheckRulesAsync(string topic, string subscription, CancellationToken cancellationToken)
    {
        var admin = Admin;
        var rules = new List<RuleProperties>();
        await foreach (var rule in admin.GetRulesAsync(topic, subscription, cancellationToken).ConfigureAwait(false))
            rules.Add(rule);

        bool hasRule = rules.Exists(r => r.Name == RuleName);
        bool onlyDefaultBesides = rules.Count == (hasRule ? 2 : 1) && rules.Exists(r => r.Name == DefaultRuleName && r.Filter is TrueRuleFilter);
        if (hasRule && !onlyDefaultBesides)
            return;

        if (onlyDefaultBesides)
        {
            if (!hasRule)
            {
                try
                {
                    await admin.CreateRuleAsync(topic, subscription, new CreateRuleOptions(RuleName, new SqlRuleFilter(GetRuleFilter(subscription))), cancellationToken).AnyContext();
                }
                catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityAlreadyExists)
                {
                    // Another process is replacing the default rule too.
                }
            }

            try
            {
                await admin.DeleteRuleAsync(topic, subscription, DefaultRuleName, cancellationToken).AnyContext();
            }
            catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
            {
            }

            _logger.LogInformation("Replaced the default rule of subscription {Subscription} of topic {Topic} with rule {Rule}, so it only gets messages sent to the topic or to it", subscription, topic, RuleName);
            return;
        }

        _logger.LogWarning("Subscription {Subscription} of topic {Topic} has its own rules, so it was left alone: messages sent to one subscription of the topic may reach it, " +
            "and messages sent to it alone may not match its rules. Add rule {Rule} with filter {Filter} to it", subscription, topic, RuleName, GetRuleFilter(subscription));
    }

    private void ValidateSessions(bool requiresSession, string entity)
    {
        if (requiresSession != _options.RequiresSession)
        {
            throw new MessageBusException(requiresSession
                ? $"{entity} requires sessions, but the transport does not use them (RequiresSession is false). Use another prefix or name, or turn RequiresSession on."
                : $"{entity} does not use sessions, which the transport needs for RequiresSession. Use another prefix or name, or delete the entity so it is created again.");
        }
    }

    private void WarnOnDuplicateDetection(bool requiresDuplicateDetection, string entity)
    {
        if (requiresDuplicateDetection == _options.RequiresDuplicateDetection)
            return;

        if (requiresDuplicateDetection)
            _logger.LogWarning("{Entity} detects duplicates, but the transport does not deduplicate: a message sent again with the same id within the window, such as a replayed dead letter, is dropped", entity);
        else
            _logger.LogWarning("{Entity} does not detect duplicates, so messages sent to it are not deduplicated although RequiresDuplicateDetection is on", entity);
    }

    /// <summary>The limit to give new entities on a Premium namespace, or null for the Standard tier's 256 KiB.</summary>
    private long? LargeMessageSizeInKilobytes => _options.MaxMessageSize > 256 * 1024 ? (_options.MaxMessageSize + 1023) / 1024 : null;

    private static async Task<TimeSpan?> GetOrCreateAsync(ConcurrentDictionary<string, Lazy<Task<TimeSpan?>>> cache, string key, Func<Task<TimeSpan?>> factory, CancellationToken cancellationToken)
    {
        var entry = cache.GetOrAdd(key, static (_, create) => new Lazy<Task<TimeSpan?>>(create), factory);
        try
        {
            return await entry.Value.WaitAsync(cancellationToken).AnyContext();
        }
        catch when (entry.Value.IsFaulted || entry.Value.IsCanceled)
        {
            cache.TryRemove(new KeyValuePair<string, Lazy<Task<TimeSpan?>>>(key, entry));
            throw;
        }
    }
}
