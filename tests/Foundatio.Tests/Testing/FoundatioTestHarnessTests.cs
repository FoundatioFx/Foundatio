using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Foundatio.Resilience;
using Foundatio.Testing;
using Foundatio.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using static Foundatio.Tests.Testing.OrdersApp;

namespace Foundatio.Tests.Testing;

public class FoundatioTestHarnessTests : TestWithLoggingBase
{
    private static readonly RetryPolicy FastRetries = new() { MaxAttempts = 3, Delay = RetryPolicy.Fixed(TimeSpan.FromMilliseconds(20)) };

    private readonly ITestOutputHelper _output;
    private readonly List<IHost> _hosts = [];

    public FoundatioTestHarnessTests(ITestOutputHelper output) : base(output)
    {
        _output = output;
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
        }

        await base.DisposeAsync();
    }

    [Fact]
    public async Task WaitForIdleAsync_AfterPublishingAnEvent_RecordsTheWholeFlow()
    {
        var host = await StartAppAsync();
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();
        var messages = host.Services.GetRequiredService<IMessageClient>();

        await messages.PublishAsync(new OrderPlaced(1, "a@b.com"), new PublishOptions { CorrelationId = "order-1" }, TestCancellationToken);
        await harness.WaitForIdleAsync(cancellationToken: TestCancellationToken);

        var published = Assert.Single(harness.PublishedMessages);
        Assert.Equal("orders.placed", published.MessageType);
        Assert.Equal("topic:orders", published.Address);

        Assert.Equal(new SendReceipt(1, "a@b.com"), Assert.Single(harness.Sent<SendReceipt>()));
        Assert.Equal(new ShipOrder(1), Assert.Single(harness.Sent<ShipOrder>()));
        Assert.All(harness.SentMessages, m => Assert.Equal("order-1", m.CorrelationId));

        Assert.Equal([MessageOutcome.Completed, MessageOutcome.Completed], harness.HandledMessages.Select(m => m.Outcome));
        Assert.Contains(harness.HandledMessages, m => m is { Message: OrderPlaced, Address: "subscription:orders/orders", Attempt: 1 });
        Assert.Equal(["a@b.com: Receipt for order 1"], host.Services.GetRequiredService<EmailSender>().Sent);

        var invoice = Assert.Single(harness.JobRuns);
        Assert.Equal(JobState.Succeeded, invoice.State);
        Assert.Equal(new GenerateInvoice(1), invoice.Arguments);
        Assert.Equal("order-1", invoice.CorrelationId);
    }

    [Fact]
    public async Task WaitForHandledAsync_WaitsForTheHandlerAndReturnsTheDelivery()
    {
        var host = await StartAppAsync();
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();

        await host.Services.GetRequiredService<IMessageClient>().SendAsync(new SendReceipt(2, "c@d.com"), new SendOptions { Headers = new Dictionary<string, string> { ["tenant"] = "acme" } }, TestCancellationToken);
        var handled = Assert.Single(await harness.WaitForHandledAsync<SendReceipt>(cancellationToken: TestCancellationToken));

        Assert.Equal(MessageOutcome.Completed, handled.Outcome);
        Assert.Equal("queue:orders.send-receipt", handled.Address);
        Assert.Equal("acme", handled.Headers["tenant"]);
        Assert.Equal(harness.SentMessages.Single().MessageId, handled.MessageId);
    }

    [Fact]
    public async Task WaitForDeadLetteredAsync_WhenAHandlerKeepsFailing_ReturnsTheDeadLetterAfterTheRetries()
    {
        var host = await StartAppAsync();
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();

        await host.Services.GetRequiredService<IMessageClient>().SendAsync(new SendReceipt(3, "bounce@b.com"), cancellationToken: TestCancellationToken);
        var deadLetter = Assert.Single(await harness.WaitForDeadLetteredAsync<SendReceipt>(cancellationToken: TestCancellationToken));

        Assert.Equal(3, deadLetter.Attempt);
        Assert.Contains("Mailbox bounce@b.com does not exist", deadLetter.Error);
        Assert.IsType<InvalidOperationException>(deadLetter.Exception);
        Assert.Equal([MessageOutcome.Retried, MessageOutcome.Retried, MessageOutcome.DeadLettered], harness.HandledMessages.Select(m => m.Outcome));
        Assert.Equal([1, 2, 3], harness.HandledMessages.Select(m => m.Attempt));
    }

    [Fact]
    public async Task WaitForHandledAsync_WhenTheMessageIsDeadLettered_TimesOutSayingWhatHappened()
    {
        var host = await StartAppAsync();
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();

        await host.Services.GetRequiredService<IMessageClient>().SendAsync(new SendReceipt(4, "bounce@b.com"), cancellationToken: TestCancellationToken);
        await harness.WaitForDeadLetteredAsync<SendReceipt>(cancellationToken: TestCancellationToken);
        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => harness.WaitForHandledAsync<SendReceipt>(timeout: TimeSpan.FromMilliseconds(100), cancellationToken: TestCancellationToken));

        Assert.Contains("1 DeadLettered", timeout.Message);
        Assert.Contains("Mailbox bounce@b.com does not exist", timeout.Message);
    }

    [Fact]
    public async Task HandledMessages_WhenATopicCarriesATypeWithNoHandler_RecordsItAsSkipped()
    {
        var host = await StartAppAsync();
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();

        await host.Services.GetRequiredService<IMessageClient>().PublishAsync(new OrderCancelled(5), cancellationToken: TestCancellationToken);
        await harness.WaitForIdleAsync(cancellationToken: TestCancellationToken);

        var skipped = Assert.Single(harness.HandledMessages);
        Assert.Equal(MessageOutcome.Skipped, skipped.Outcome);
        Assert.Equal(new OrderCancelled(5), skipped.Message);
    }

    [Fact]
    public async Task WaitForIdleAsync_WaitsForInstanceSubscribers()
    {
        var host = await StartAppAsync();
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();

        await host.Services.GetRequiredService<IMessageClient>().PublishBatchAsync([new PriceChanged("sku-1"), new PriceChanged("sku-2")], cancellationToken: TestCancellationToken);
        await harness.WaitForIdleAsync(cancellationToken: TestCancellationToken);

        Assert.Equal(["sku-1", "sku-2"], host.Services.GetRequiredService<PriceCache>().Invalidated.Order());
        Assert.All(harness.HandledMessages, m => Assert.StartsWith("instance:catalog.price-changed/", m.Address));
    }

    [Fact]
    public async Task WaitForIdleAsync_WhenAHandlerIsStuck_TimesOutNamingTheBusySource()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = await StartAppAsync(services => services.AddFoundatioMessaging(m => m.AddConsumer<ShipOrder>((_, _, ct) => release.Task.WaitAsync(ct))));
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();

        await host.Services.GetRequiredService<IMessageClient>().SendAsync(new ShipOrder(6), cancellationToken: TestCancellationToken);
        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => harness.WaitForIdleAsync(TimeSpan.FromMilliseconds(200), TestCancellationToken));
        release.SetResult();
        await harness.WaitForIdleAsync(cancellationToken: TestCancellationToken);

        Assert.Contains("queue:shipping.ship-order", timeout.Message);
        Assert.Equal(MessageOutcome.Completed, Assert.Single(harness.HandledMessages).Outcome);
    }

    [Fact]
    public async Task WaitForIdleAsync_WhenTheHostWasNotStarted_SaysSo()
    {
        var host = BuildApp();
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();

        await host.Services.GetRequiredService<IMessageClient>().SendAsync(new SendReceipt(7, "a@b.com"), cancellationToken: TestCancellationToken);
        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => harness.WaitForIdleAsync(TimeSpan.FromMilliseconds(100), TestCancellationToken));

        Assert.Contains("queue:orders.send-receipt has 1 message(s)", timeout.Message);
        Assert.Contains("has the host started", timeout.Message);
    }

    [Fact]
    public async Task Sent_RecordsTheRequestedDeliveryTimeOfDelayedMessages()
    {
        var host = await StartAppAsync();
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();
        var before = DateTimeOffset.UtcNow;

        await host.Services.GetRequiredService<IMessageClient>().SendAsync(new ShipOrder(8), new SendOptions { Delay = TimeSpan.FromHours(1) }, TestCancellationToken);

        var sent = Assert.Single(harness.SentMessages);
        Assert.InRange(sent.DeliverAt!.Value, before.AddHours(1), DateTimeOffset.UtcNow.AddHours(1));
        Assert.Null(sent.Outcome);
        Assert.Equal(0, sent.Attempt);
    }

    [Fact]
    public async Task WaitForJobAsync_ReturnsFinishedRunsIncludingFailures()
    {
        var host = await StartAppAsync();
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();

        await host.Services.GetRequiredService<IJobClient>().EnqueueAsync(new GenerateInvoice(9, Fail: true), cancellationToken: TestCancellationToken);
        var run = Assert.Single(await harness.WaitForJobAsync<GenerateInvoice>(cancellationToken: TestCancellationToken));

        Assert.Equal(JobState.Failed, run.State);
        Assert.Equal(3, run.Attempt);
        Assert.Contains("No billing address for order 9", run.Error);
        Assert.Equal([JobState.Queued, JobState.Queued, JobState.Failed], harness.JobRuns.Select(r => r.State));
        Assert.Same(run, Assert.Single(await harness.WaitForJobAsync<GenerateInvoiceJob>(cancellationToken: TestCancellationToken)));
    }

    [Fact]
    public async Task TriggerCronJobAsync_RunsTheScheduledJob_WhileSchedulesDoNotFireOnTheirOwn()
    {
        var host = await StartAppAsync();
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();

        await harness.WaitForIdleAsync(cancellationToken: TestCancellationToken);
        Assert.Empty(harness.JobRuns);

        var handle = await harness.TriggerCronJobAsync<NightlyCleanupJob>(TestCancellationToken);
        var run = Assert.Single(await harness.WaitForJobAsync<NightlyCleanupJob>(cancellationToken: TestCancellationToken));

        Assert.Equal(handle.Id, run.JobId);
        Assert.Equal(JobState.Succeeded, run.State);
        Assert.Equal("cleaned", run.Message);
        Assert.Equal("orders.nightly-cleanup", run.ScheduleName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.TriggerCronJobAsync<GenerateInvoice>(TestCancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.TriggerCronJobAsync("nope", TestCancellationToken));
    }

    [Fact]
    public async Task WaitForIdleAsync_WhenAJobNothingInTheAppRunsIsQueued_TimesOutNamingTheJob()
    {
        var host = await StartAppAsync();
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();

        await host.Services.GetRequiredService<IJobClient>().EnqueueAsync(new ArchiveOrders(30), cancellationToken: TestCancellationToken);
        var error = await Assert.ThrowsAsync<TimeoutException>(() => harness.WaitForIdleAsync(TimeSpan.FromSeconds(1), TestCancellationToken));

        Assert.Contains(typeof(ArchiveOrders).FullName!.Replace('+', '.'), error.Message);
        Assert.Contains("nothing in this app runs it", error.Message);
    }

    public record ArchiveOrders(int OlderThanDays);

    private async Task<IHost> StartAppAsync(Action<IServiceCollection>? configureTest = null)
    {
        var host = BuildApp(configureTest);
        await host.StartAsync(TestCancellationToken);
        return host;
    }

    private IHost BuildApp(Action<IServiceCollection>? configureTest = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders().AddTestLogger(_output);
        builder.Services.AddOrdersApp();
        builder.Services.AddFoundatioMessaging(m => m.Configure(o => o.DefaultRetryPolicy = FastRetries));
        builder.Services.AddFoundatioJobs(j => j.Configure(o => o.DefaultRetryPolicy = FastRetries));
        configureTest?.Invoke(builder.Services);
        builder.Services.AddFoundatioTestHarness();

        var host = builder.Build();
        _hosts.Add(host);
        return host;
    }
}
