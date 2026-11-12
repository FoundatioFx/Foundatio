using Foundatio.Jobs;
using Foundatio.Jobs.Store;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
using Foundatio.Testing;
using StackExchange.Redis;

namespace V14App;

// The app-facing messaging and jobs API, used the way an app does. This project does not suppress FOUNDATIO0001, so
// it fails to build if any of it requires the [Experimental] provider API.

public record OrderPlaced(int OrderId);

public record SendReceipt(int OrderId);

public record GenerateReport(string Format);

public class OrderPlacedHandler(IMessageClient messages) : IMessageHandler<OrderPlaced>
{
    public async Task HandleAsync(OrderPlaced message, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            await messages.SendAsync(new SendReceipt(message.OrderId), new SendOptions { CorrelationId = context.CorrelationId, GroupKey = context.GroupKey }, cancellationToken);
        }
        catch (MessageTooLargeException)
        {
            context.DeadLetter("The receipt is too large to send.");
        }
    }
}

public class GenerateReportJob : IDurableJob<GenerateReport>
{
    public Task RunAsync(GenerateReport arguments, IJobContext context, CancellationToken cancellationToken) =>
        context.ReportProgressAsync(100, arguments.Format, cancellationToken);
}

public class CleanupJob : IDurableJob
{
    public Task RunAsync(IJobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class Registrations
{
    public static void UseProviders(IServiceCollection services, IConnectionMultiplexer redis)
    {
        services.AddFoundatioMessaging(m => m
            .UseServiceName("orders")
            .UseRedis(redis, new RedisMessageTransportOptions { Prefix = "orders" })
            .Configure(o => o.RequireTransportCapabilities = true)
            .AddSubscriber<OrderPlacedHandler>(new SubscriptionOptions { Scope = SubscriptionScope.Service })
            .AddConsumer<SendReceipt>((message, context, cancellationToken) => Task.CompletedTask));
        services.AddFoundatioMessaging(m => m.UseSqs(new SqsMessageTransportOptions { ConnectionString = "serviceurl=http://localhost:4566" }));
        services.AddFoundatioMessaging(m => m.UseKafka("localhost:9092"));
        services.AddFoundatioJobs(j => j
            .UseRedis(redis, new RedisJobStoreOptions { Prefix = "orders" })
            .AddJob<GenerateReportJob>()
            .AddCronJob<CleanupJob>("0 * * * *"));
        services.AddHealthChecks().AddFoundatioMessaging().AddFoundatioJobs();
    }

    public static void UseSharedInstances(IServiceCollection services, IConnectionMultiplexer redis)
    {
        var transport = new InMemoryMessageTransport(new TransportCapabilities { OrderedGroups = false, Deduplication = false, MaxMessageSize = 256 * 1024 });
        services.AddFoundatioMessaging(m => m.UseTransport(transport));
        services.AddFoundatioMessaging(m => m.UseTransport(_ => new RedisMessageTransport(redis)));
        services.AddFoundatioJobs(j => j.UseStore(new InMemoryJobStore()));
        services.AddFoundatioJobs(j => j.UseStore(_ => new RedisJobStore(redis)));
    }

    public static void UseTestHarness(IServiceCollection services)
    {
        services.AddFoundatioTestHarness(o => o.TransportCapabilities = new TransportCapabilities { OrderedGroups = false });
    }
}

public static class AppUsage
{
    public static async Task<int> ReplayFailedReceiptsAsync(IMessageAdmin admin)
    {
        var source = MessageSource.Queue("orders.send-receipt");
        IReadOnlyList<DeadLetterMessage> failed = await admin.PeekDeadLettersAsync(source);
        if (failed.Count == 0)
            return 0;

        await admin.DeleteAsync(MessageSource.Subscription("orders.order-placed", "retired-service"));
        return await admin.ReplayDeadLettersAsync(source);
    }

    public static async Task<JobState?> EnqueueReportAsync(IJobClient jobs)
    {
        JobHandle handle = await jobs.EnqueueAsync(new GenerateReport("pdf"));
        JobDetails completed = await handle.WaitForCompletionAsync(TimeSpan.FromSeconds(10));
        JobPage<JobDetails> failed = await jobs.QueryAsync(new JobQuery { State = JobState.Failed, Name = completed.Name });
        return failed.Jobs.FirstOrDefault()?.State;
    }

    public static async Task AssertReceiptSentAsync(IServiceProvider services, IMessageClient messages)
    {
        var harness = services.GetRequiredService<FoundatioTestHarness>();
        await messages.PublishAsync(new OrderPlaced(1));
        await harness.WaitForIdleAsync();
        RecordedMessage sent = harness.SentMessages.Single();
        if (harness.Sent<SendReceipt>().Single().OrderId != 1 || sent.Outcome is MessageOutcome.DeadLettered)
            throw new InvalidOperationException("The receipt was not sent.");

        var context = new TestMessageContext();
        await new OrderPlacedHandler(messages).HandleAsync(new OrderPlaced(2), context, CancellationToken.None);
        var jobContext = new TestJobContext();
        await new GenerateReportJob().RunAsync(new GenerateReport("csv"), jobContext, CancellationToken.None);
    }
}
