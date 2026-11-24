using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Azure.Storage.Queues;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Tests.Utility;
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Foundatio.AzureStorage.Tests.Messaging;

/// <summary>
/// Queues on Storage Queues, which are durable and cheap, and events and the legacy bus on Redis, which pushes them:
/// the split an app with both makes with <see cref="FoundatioMessagingBuilder.ForTopics"/>.
/// </summary>
public class StorageQueuesWithRedisTopicsTests(ITestOutputHelper output) : TestWithLoggingBase(output)
{
    [Fact]
    public async Task ForTopics_WithRedis_KeepsQueuesOnStorageAndEventsAndTheLegacyBusOnRedis()
    {
        // Arrange
        string prefix = $"fs{Guid.NewGuid():N}"[..10] + "-";
        var posted = new TaskCompletionSource<EventPost>(TaskCreationOptions.RunContinuationsAsynchronously);
        var changed = new TaskCompletionSource<EntityChanged>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pushed = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource<CacheLockReleased>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Log);
        services.AddFoundatioMessaging(m => m
            .UseAzureStorageQueues(new AzureStorageQueueMessageTransportOptions { ConnectionString = AzureStorageQueueMessageTransportTests.GetConnectionString(), Prefix = prefix })
            .ForTopics(t => t.UseRedis(GetRedisConnectionString(), new RedisMessageTransportOptions { Prefix = $"foundatio:{prefix}" }))
            .UseServiceName("exl")
            .AddLegacyMessageBus()
            .AddConsumer<EventPost>((post, _, _) =>
            {
                posted.TrySetResult(post);
                return Task.CompletedTask;
            })
            .AddSubscriber<EntityChanged>((change, _, _) =>
            {
                changed.TrySetResult(change);
                return Task.CompletedTask;
            })
            .AddSubscriber<EntityChanged>((_, _, _) =>
            {
                pushed.TrySetResult(Stopwatch.GetTimestamp());
                return Task.CompletedTask;
            }, new SubscriptionOptions { Scope = SubscriptionScope.Instance, Name = "exl-websockets" }));
        await using var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToList();
        foreach (var hostedService in hostedServices)
            await hostedService.StartAsync(TestCancellationToken);

        var admin = provider.GetRequiredService<IMessageAdmin>();
        var storage = new QueueServiceClient(AzureStorageQueueMessageTransportTests.GetConnectionString());
        try
        {
            var bus = provider.GetRequiredService<IMessageBus>();
            await bus.SubscribeAsync<CacheLockReleased>(message => released.TrySetResult(message), TestCancellationToken);
            await WaitForRunningAsync(provider.GetRequiredService<MessagingStatus>());

            // Act
            var client = provider.GetRequiredService<IMessageClient>();
            await client.SendAsync(new EventPost("project-1"), cancellationToken: TestCancellationToken);
            long publishedAt = Stopwatch.GetTimestamp();
            await client.PublishAsync(new EntityChanged("project-1"), cancellationToken: TestCancellationToken);
            await bus.PublishAsync(new CacheLockReleased("lock-1"), cancellationToken: TestCancellationToken);

            // Assert
            Assert.Equal("project-1", (await posted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestCancellationToken)).ProjectId);
            Assert.Equal("project-1", (await changed.Task.WaitAsync(TimeSpan.FromSeconds(30), TestCancellationToken)).Id);
            Assert.Equal("lock-1", (await released.Task.WaitAsync(TimeSpan.FromSeconds(30), TestCancellationToken)).Resource);
            var latency = Stopwatch.GetElapsedTime(publishedAt, await pushed.Task.WaitAsync(TimeSpan.FromSeconds(30), TestCancellationToken));
            _logger.LogInformation("The instance subscriber got the event {Latency} ms after the publish", latency.TotalMilliseconds);

            var queues = new List<string>();
            await foreach (var queue in storage.GetQueuesAsync(prefix: prefix, cancellationToken: TestCancellationToken))
                queues.Add(queue.Name);
            Assert.NotEmpty(queues);
            Assert.All(queues, name => Assert.StartsWith(prefix + "exl-event-post-", name, StringComparison.Ordinal));

            var subscription = admin.GetSubscription<EntityChanged>();
            Assert.Equal(subscription, (await admin.GetStatsAsync(subscription, TestCancellationToken))?.Source);
        }
        finally
        {
            foreach (var hostedService in Enumerable.Reverse(hostedServices))
                await hostedService.StopAsync(TestCancellationToken);

            await admin.DeleteAsync(admin.GetSubscription<EntityChanged>(), TestCancellationToken);
            await foreach (var queue in storage.GetQueuesAsync(prefix: prefix, cancellationToken: TestCancellationToken))
                await storage.DeleteQueueAsync(queue.Name, TestCancellationToken);
        }
    }

    private static string GetRedisConnectionString() =>
        Configuration.GetConnectionString("RedisConnectionString") ?? "127.0.0.1:7005,abortConnect=false,allowAdmin=true";

    private async Task WaitForRunningAsync(MessagingStatus status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!status.GetProcessors().All(p => p.State == Foundatio.Utility.ProcessingState.Running))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The message processors did not start in time.");

            await Task.Delay(50, TestCancellationToken);
        }
    }

    [Message("exl.event-post")]
    public sealed record EventPost(string ProjectId);

    [Message("exl.entity-changed")]
    public sealed record EntityChanged(string Id);

    public sealed record CacheLockReleased(string Resource);
}
