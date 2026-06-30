using System;

namespace Foundatio.Messaging;

/// <summary>
/// The single, shared convention for addressing a pub/sub subscription as one transport destination string:
/// <c>"{topic}/{subscription}"</c>. The topic is part of the identity so the same subscription name used on two topics
/// resolves to two distinct sources.
/// </summary>
/// <remarks>
/// The resulting string (the <c>source</c> passed to receive/subscribe and carried as <see cref="TransportEntry.Destination"/>)
/// is an <b>opaque provider-agnostic key</b>: because it contains <c>'/'</c> a transport must NOT assume it is a legal
/// broker resource name (e.g. an SQS queue name). Map it to native resources during
/// <see cref="ISupportsProvisioning.EnsureAsync"/> — which also supplies the structured topic via
/// <see cref="DestinationDeclaration.Source"/> — and treat it as a dictionary key thereafter, or parse it with
/// <see cref="TryParse"/>. Topic and subscription names must not contain <c>'/'</c>. Centralizing the convention here
/// (rather than each provider re-deriving it) keeps providers interoperable.
/// </remarks>
public static class SubscriptionAddress
{
    /// <summary>Formats the topic-qualified subscription destination key.</summary>
    public static string Format(string topic, string subscription) => $"{topic}/{subscription}";

    /// <summary>
    /// Splits a destination produced by <see cref="Format"/> into its topic and subscription. Returns false for a bare
    /// (non-subscription) destination, leaving <paramref name="topic"/> = the whole input and <paramref name="subscription"/> empty.
    /// </summary>
    public static bool TryParse(string destination, out string topic, out string subscription)
    {
        ArgumentNullException.ThrowIfNull(destination);
        int slash = destination.IndexOf('/');
        if (slash <= 0 || slash >= destination.Length - 1)
        {
            topic = destination;
            subscription = "";
            return false;
        }

        topic = destination[..slash];
        subscription = destination[(slash + 1)..];
        return true;
    }
}
