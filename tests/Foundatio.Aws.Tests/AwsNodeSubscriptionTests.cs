using System;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Xunit;

namespace Foundatio.Aws.Tests;

public class AwsNodeSubscriptionTests
{
    [Fact]
    public async Task Nodes_ReceiveIndependentCopies_AndDisposeTheirResources()
    {
        string? connection = Environment.GetEnvironmentVariable("FOUNDATIO_AWS_CONNECTION_STRING");
        Assert.SkipWhen(String.IsNullOrEmpty(connection), "FOUNDATIO_AWS_CONNECTION_STRING not set.");
        var options = AwsMessageTransportOptions.FromConnectionString(connection);
        options.ResourcePrefix = $"node-test-{Guid.NewGuid():N}-";
        await using var transport = new AwsMessageTransport(options);
        await using var bus = new MessageBus(transport, new MessageBusOptions { OwnsTransport = false });
        var token = TestContext.Current.CancellationToken;
        var firstReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = await bus.SubscribeNodeAsync((_, _) => { firstReceived.TrySetResult(); return Task.CompletedTask; }, new() { Topic = "events", NodeId = "first" }, token);
        var second = await bus.SubscribeNodeAsync((_, _) => { secondReceived.TrySetResult(); return Task.CompletedTask; }, new() { Topic = "events", NodeId = "second" }, token);
        try
        {
            Assert.NotEqual(first.Source, second.Source);
            await bus.PublishAsync("changed", new MessagePublishOptions { Topic = "events" }, token);
            await Task.WhenAll(firstReceived.Task, secondReceived.Task).WaitAsync(TimeSpan.FromSeconds(15), token);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
            await transport.DeleteAsync(DestinationAddress.ForTopic("events"), token);
        }
        Assert.False(await transport.ExistsAsync(first.Source, token));
        Assert.False(await transport.ExistsAsync(second.Source, token));
    }
}
