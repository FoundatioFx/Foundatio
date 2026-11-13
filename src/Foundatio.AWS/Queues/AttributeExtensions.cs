using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace Foundatio.Queues;

public static class AttributeExtensions
{
    /// <summary>
    /// Application attribute that carries <see cref="QueueEntryOptions.CorrelationId"/>; it is not exposed in <see cref="IQueueEntry.Properties"/>.
    /// </summary>
    internal const string CorrelationIdAttributeName = "CorrelationId";

    public static int ApproximateReceiveCount(this IDictionary<string, string> attributes)
    {
        if (attributes is null)
            return 0;

        if (!attributes.TryGetValue("ApproximateReceiveCount", out string? v))
            return 0;

        Int32.TryParse(v, out int value);
        return value;
    }

    public static DateTime SentTimestamp(this IDictionary<string, string> attributes)
    {
        // message was sent to the queue (epoch time in milliseconds)
        if (attributes is null)
            return DateTime.MinValue;

        if (!attributes.TryGetValue("SentTimestamp", out string? v))
            return DateTime.MinValue;

        if (!Int64.TryParse(v, out long value))
            return DateTime.MinValue;

        return DateTimeOffset.FromUnixTimeMilliseconds(value).DateTime;
    }

    public static string? CorrelationId(this IDictionary<string, MessageAttributeValue> attributes)
    {
        if (attributes is null)
            return null;

        if (!attributes.TryGetValue(CorrelationIdAttributeName, out var v))
            return null;

        return v.StringValue;
    }

    /// <summary>
    /// Gets the <c>MessageGroupId</c> system attribute of a received message, or <c>null</c> when the message has no group.
    /// </summary>
    public static string? MessageGroupId(this IDictionary<string, string>? attributes)
    {
        if (attributes is null)
            return null;

        return attributes.TryGetValue(MessageSystemAttributeName.MessageGroupId, out string? groupId) ? groupId : null;
    }

    public static string? RedrivePolicy(this IDictionary<string, string> attributes)
    {
        if (attributes is null)
            return null;

        if (!attributes.TryGetValue("RedrivePolicy", out string? v))
            return null;

        return v;
    }

    public static string? DeadLetterQueue(this IDictionary<string, string> attributes)
    {
        if (attributes is null)
            return null;

        if (!attributes.TryGetValue("RedrivePolicy", out string? v))
            return null;

        if (String.IsNullOrEmpty(v))
            return null;

        using var redrivePolicy = JsonDocument.Parse(v);
        if (!redrivePolicy.RootElement.TryGetProperty("deadLetterTargetArn", out var arnElement))
            return null;

        string? arn = arnElement.GetString();
        if (String.IsNullOrEmpty(arn))
            return null;

        string[] parts = arn.Split(':');
        return parts.LastOrDefault();
    }
}
