using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Foundatio.Messaging.Transport;
using Foundatio.Resilience;

namespace Foundatio.Messaging;

/// <summary>Checks registered handlers when the messaging runtime is built, so mistakes fail at startup rather than in production.</summary>
internal static class HandlerRegistrationValidator
{
    /// <summary>
    /// Throws when a durable subscription would be named after a default service name, or when handlers that share a
    /// source, and so share each delivery, set different delivery settings.
    /// </summary>
    public static void Validate(MessagingRuntime runtime, IReadOnlyList<MessageHandlerRegistration> handlers)
    {
        if (!runtime.CanNameSubscriptionsByDefault)
        {
            var unnamed = handlers.Where(IsUnnamedServiceSubscription).ToList();
            if (unnamed.Count > 0)
                throw CreateMissingServiceNameException(runtime, unnamed);
        }

        foreach (var source in handlers.GroupBy(runtime.GetSource))
        {
            var first = source.First();
            foreach (var other in source.Skip(1))
            {
                if (FindDifference(first.Options, other.Options) is { } difference)
                    throw new InvalidOperationException(DescribeConflict(source.Key, first, other, difference));
            }
        }
    }

    public static InvalidOperationException CreateMissingServiceNameException(MessagingRuntime runtime, IEnumerable<MessageHandlerRegistration> subscriptions)
    {
        string list = String.Join(", ", subscriptions.Select(h => $"{h.HandlerName} (topic {runtime.Types.GetRoute(h.MessageType).Topic})").Distinct());
        return new InvalidOperationException(
            $"Set a service name with UseServiceName(\"orders\") inside AddFoundatioMessaging, or with MessagingOptions.ServiceName. " +
            $"It names the durable subscriptions of {list}, and {runtime.Transport.GetType().Name} keeps them under that name: services that share a name split each other's events, " +
            $"and renaming one starts a new subscription. Only the in-memory transport defaults it to the application name (\"{runtime.ServiceName}\"). " +
            "Alternatively, give each subscriber its own SubscriptionOptions.Name.");
    }

    private static bool IsUnnamedServiceSubscription(MessageHandlerRegistration handler)
        => handler is { Kind: MessageHandlerKind.Subscriber, Scope: SubscriptionScope.Service, SubscriptionName: null };

    /// <summary>The first setting the two option sets give different values, compared as written: unset differs from any value.</summary>
    private static (string Setting, string First, string? Other)? FindDifference(HandlerOptions first, HandlerOptions other)
    {
        if (!Equals(first.RetryPolicy, other.RetryPolicy))
        {
            return first.RetryPolicy is { } a && other.RetryPolicy is { } b && a.MaxAttempts == b.MaxAttempts
                ? (nameof(HandlerOptions.RetryPolicy), $"two policies with MaxAttempts {a.MaxAttempts} but different {(Equals(a.Delay, b.Delay) ? "ShouldRetry" : "Delay")} delegates; share one RetryPolicy instance, or build both with RetryPolicy.Exponential, Fixed and DoNotRetry, which return the same delegate for the same arguments", null)
                : (nameof(HandlerOptions.RetryPolicy), Describe(first.RetryPolicy), Describe(other.RetryPolicy));
        }
        if (first.LeaseDuration != other.LeaseDuration)
            return (nameof(HandlerOptions.LeaseDuration), Describe(first.LeaseDuration), Describe(other.LeaseDuration));
        if (first.MaxConcurrency != other.MaxConcurrency)
            return (nameof(HandlerOptions.MaxConcurrency), Describe(first.MaxConcurrency), Describe(other.MaxConcurrency));

        return null;
    }

    private static string DescribeConflict(MessageSource source, MessageHandlerRegistration first, MessageHandlerRegistration other, (string Setting, string First, string? Other) difference)
    {
        string handlers = first.HandlerName == other.HandlerName ? $"Two {first.HandlerName} handlers" : $"{first.HandlerName} and {other.HandlerName}";
        string kind = source.Kind == MessageSourceKind.Queue ? "a queue" : "a subscription";
        string fix = source.Kind switch
        {
            MessageSourceKind.Queue => "Give them the same settings, or send one of the message types to a queue of its own with [Message(Queue = ...)] or MapMessage.",
            MessageSourceKind.InstanceSubscription => "Give them the same settings, or give one its own SubscriptionOptions.Name so it gets an instance subscription of its own.",
            _ => "Give them the same settings, or give one its own SubscriptionOptions.Name so it gets a subscription of its own."
        };

        string values = difference.Other is null ? difference.First : $"{difference.First} and {difference.Other}";
        return $"{handlers} share {source} but set different {difference.Setting} values ({values}). " +
            $"Handlers that share {kind} share each delivery: one RetryPolicy, LeaseDuration and MaxConcurrency apply to all of them, " +
            $"and when one throws, all of them run again. {fix}";
    }

    private static string Describe(TimeSpan? value) => value?.ToString() ?? "not set";

    private static string Describe(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "not set";

    private static string Describe(RetryPolicy? policy) => policy is null ? "not set" : $"MaxAttempts {policy.MaxAttempts}";
}
