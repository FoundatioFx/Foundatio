using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Foundatio.Messaging.Transport;

/// <summary>
/// Adds or removes the statement allowing an SNS topic to send to a queue, keeping every other statement in the policy.
/// </summary>
internal static class SqsMessageTransportPolicy
{
    /// <summary>Returns the policy to set, or <c>null</c> when the existing policy already allows the topic.</summary>
    public static string? Merge(string? existingPolicy, string queueArn, string topicArn)
    {
        var statement = CreateStatement(queueArn, topicArn);
        var (policy, statements) = Parse(existingPolicy, queueArn, topicArn) ?? (new JsonObject { ["Version"] = "2012-10-17" }, new JsonArray());
        policy["Statement"] = statements;

        int index = IndexOf(statements, statement["Sid"]!.GetValue<string>());
        if (index < 0)
        {
            statements.Add(statement);
            return policy.ToJsonString();
        }

        if (JsonNode.DeepEquals(statements[index], statement))
            return null;

        statements[index] = statement;
        return policy.ToJsonString();
    }

    /// <summary>
    /// Returns the policy to set without the topic's statement (an empty string when nothing else is left, which removes
    /// the policy), or <c>null</c> when the policy does not allow the topic.
    /// </summary>
    public static string? Remove(string? existingPolicy, string queueArn, string topicArn)
    {
        if (Parse(existingPolicy, queueArn, topicArn) is not { } parsed)
            return null;

        var (policy, statements) = parsed;
        int index = IndexOf(statements, CreateStatement(queueArn, topicArn)["Sid"]!.GetValue<string>());
        if (index < 0)
            return null;

        statements.RemoveAt(index);
        if (statements.Count == 0)
            return String.Empty;

        policy["Statement"] = statements;
        return policy.ToJsonString();
    }

    private static (JsonObject Policy, JsonArray Statements)? Parse(string? existingPolicy, string queueArn, string topicArn)
    {
        if (String.IsNullOrWhiteSpace(existingPolicy))
            return null;

        JsonObject? policy;
        try
        {
            policy = JsonNode.Parse(existingPolicy) as JsonObject;
        }
        catch (JsonException)
        {
            policy = null;
        }

        if (policy is null)
            throw new MessageBusException($"The policy of queue {queueArn} is not a JSON object, so the transport cannot change whether topic {topicArn} may send to it.");

        var statements = policy["Statement"] switch
        {
            JsonArray array => array,
            JsonObject single => new JsonArray(single.DeepClone()),
            _ => new JsonArray()
        };

        return (policy, statements);
    }

    private static int IndexOf(JsonArray statements, string sid)
    {
        for (int i = 0; i < statements.Count; i++)
        {
            if (statements[i] is JsonObject existing && existing["Sid"]?.GetValueKind() == JsonValueKind.String && existing["Sid"]!.GetValue<string>() == sid)
                return i;
        }

        return -1;
    }

    private static JsonObject CreateStatement(string queueArn, string topicArn) => new()
    {
        ["Sid"] = $"FoundatioTopic{Hash(topicArn)}",
        ["Effect"] = "Allow",
        ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
        ["Action"] = "sqs:SendMessage",
        ["Resource"] = queueArn,
        ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = topicArn } }
    };

    private static string Hash(string value)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(value), hash);
        return Convert.ToHexString(hash[..8]);
    }
}
