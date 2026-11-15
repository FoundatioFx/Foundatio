using System.Text.Json;
using Foundatio.Messaging.Transport;
using Xunit;

namespace Foundatio.RabbitMQ.Tests.Messaging;

public class RabbitMQDelayInspectorTests
{
    [Theory]
    [InlineData("""{ "type": "quorum", "arguments": {}, "effective_policy_definition": { "max-length": 1, "overflow": "reject-publish" }, "policy": "limits" }""", "1 message")]
    [InlineData("""{ "type": "quorum", "arguments": { "x-max-length-bytes": 1024 }, "effective_policy_definition": { "overflow": "reject-publish" } }""", "1024 bytes")]
    [InlineData("""{ "type": "quorum", "arguments": { "x-overflow": "drop-head", "x-max-length": 50 }, "effective_policy_definition": { "max-length": 10, "overflow": "reject-publish" } }""", "10 messages")]
    [InlineData("""{ "type": "classic", "arguments": {}, "effective_policy_definition": { "max-length": 5, "overflow": "reject-publish-dlx" } }""", "5 messages")]
    public void DescribeTarget_ForAQueueThatRefusesWhenFull_NamesItsLimit(string json, string limit)
    {
        using var queue = JsonDocument.Parse(json);

        string? problem = RabbitMQDelayInspector.DescribeTarget(queue.RootElement, "app");

        Assert.NotNull(problem);
        Assert.Contains($"once it holds {limit}", problem);
        Assert.Contains("\"app\"", problem);
    }

    [Theory]
    [InlineData("""{ "type": "quorum", "arguments": { "x-queue-type": "quorum", "x-delivery-limit": -1 }, "effective_policy_definition": [] }""")]
    [InlineData("""{ "type": "quorum", "arguments": {}, "effective_policy_definition": { "max-length": 1000 } }""")]
    [InlineData("""{ "type": "quorum", "arguments": {}, "effective_policy_definition": { "overflow": "reject-publish" } }""")]
    [InlineData("""{ "type": "quorum", "arguments": {}, "effective_policy_definition": { "max-length": 1, "overflow": "reject-publish-dlx" } }""")]
    [InlineData("""{ "type": "classic", "arguments": { "x-overflow": "drop-head" }, "effective_policy_definition": { "max-length": 1, "overflow": "reject-publish" } }""")]
    public void DescribeTarget_ForAQueueThatTakesEveryMessage_ReturnsNull(string json)
    {
        using var queue = JsonDocument.Parse(json);

        Assert.Null(RabbitMQDelayInspector.DescribeTarget(queue.RootElement, "app"));
    }

    [Fact]
    public void HasPolicyDetails_BeforeTheQueuesFirstStatistics_IsFalse()
    {
        using var fresh = JsonDocument.Parse("""{ "name": "app.queue.orders", "type": "quorum", "arguments": {} }""");
        using var listed = JsonDocument.Parse("""{ "name": "app.queue.orders", "type": "quorum", "arguments": {}, "effective_policy_definition": {}, "policy": null }""");

        Assert.False(RabbitMQDelayInspector.HasPolicyDetails(fresh.RootElement));
        Assert.True(RabbitMQDelayInspector.HasPolicyDetails(listed.RootElement));
    }

    [Fact]
    public void DescribeDiscard_WithAPolicyThatRefusesWhenFull_WarnsThatItRefusesAllButOneMessage()
    {
        using var queue = JsonDocument.Parse("""{ "type": "quorum", "arguments": { "x-queue-type": "quorum", "x-max-length": 0 }, "effective_policy_definition": { "overflow": "reject-publish" }, "policy": "all" }""");
        using var declared = JsonDocument.Parse("""{ "type": "quorum", "arguments": { "x-queue-type": "quorum", "x-max-length": 0 }, "effective_policy_definition": [] }""");

        string? problem = RabbitMQDelayInspector.DescribeDiscard(queue.RootElement);

        Assert.NotNull(problem);
        Assert.Contains("keeps one message and refuses every one after it", problem);
        Assert.Contains("policy all", problem);
        Assert.Null(RabbitMQDelayInspector.DescribeDiscard(declared.RootElement));
    }

    [Theory]
    [InlineData("""{ "max-length": 100 }""", "refuses messages once it holds 100 messages")]
    [InlineData("""{ "overflow": "drop-head" }""", "dead-letters at most once")]
    [InlineData("""{ "overflow": "reject-publish-dlx" }""", "dead-letters at most once")]
    [InlineData("""{ "message-ttl": 60000 }""", "expires messages after 60000 ms instead of 128000 ms")]
    [InlineData("""{ "expires": 3600000 }""", "is deleted, with its delayed messages, after 3600000 ms")]
    public void DescribeLevel_WithAPolicyThatChangesIt_SaysWhatItBreaks(string policy, string expected)
    {
        using var queue = JsonDocument.Parse($$"""{ "type": "quorum", "arguments": {{LevelArguments(7)}}, "effective_policy_definition": {{policy}}, "policy": "broad" }""");

        string? problem = RabbitMQDelayInspector.DescribeLevel(queue.RootElement, 7);

        Assert.NotNull(problem);
        Assert.Contains(expected, problem);
        Assert.Contains("policy broad", problem);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{ "message-ttl": 128000, "overflow": "reject-publish" }""")]
    [InlineData("""{ "delivery-limit": 5 }""")]
    public void DescribeLevel_AsDeclaredOrWithHarmlessPolicies_ReturnsNull(string policy)
    {
        using var queue = JsonDocument.Parse($$"""{ "type": "quorum", "arguments": {{LevelArguments(7)}}, "effective_policy_definition": {{policy}} }""");

        Assert.Null(RabbitMQDelayInspector.DescribeLevel(queue.RootElement, 7));
    }

    private static string LevelArguments(int level) =>
        $$"""{ "x-queue-type": "quorum", "x-message-ttl": {{RabbitMQDelayLevels.TimeToLiveMilliseconds(level)}}, "x-dead-letter-exchange": "app.delay.06", "x-dead-letter-strategy": "at-least-once", "x-overflow": "reject-publish" }""";
}
