using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Foundatio.Messaging.Transport;

/// <summary>Adds a statement allowing an SNS topic to send to a queue, keeping every other statement in the policy.</summary>
internal static class SqsMessageTransportPolicy
{
    /// <summary>Returns the policy to set, or <c>null</c> when the existing policy already allows the topic.</summary>
    public static string? Merge(string? existingPolicy, string queueArn, string topicArn)
    {
        var statement = CreateStatement(queueArn, topicArn);
        string sid = statement["Sid"]!.GetValue<string>();

        JsonObject? policy = null;
        if (!String.IsNullOrWhiteSpace(existingPolicy))
        {
            try
            {
                policy = JsonNode.Parse(existingPolicy) as JsonObject;
            }
            catch (JsonException)
            {
                throw new MessageBusException($"The policy of queue {queueArn} is not valid JSON, so the transport cannot allow topic {topicArn} to send to it.");
            }
        }

        policy ??= new JsonObject { ["Version"] = "2012-10-17" };
        var statements = policy["Statement"] switch
        {
            JsonArray array => array,
            JsonObject single => new JsonArray(single.DeepClone()),
            _ => new JsonArray()
        };
        policy["Statement"] = statements;

        for (int i = 0; i < statements.Count; i++)
        {
            if (statements[i] is not JsonObject existing || existing["Sid"]?.GetValueKind() != JsonValueKind.String || existing["Sid"]!.GetValue<string>() != sid)
                continue;

            if (JsonNode.DeepEquals(existing, statement))
                return null;

            statements[i] = statement;
            return policy.ToJsonString();
        }

        statements.Add(statement);
        return policy.ToJsonString();
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
