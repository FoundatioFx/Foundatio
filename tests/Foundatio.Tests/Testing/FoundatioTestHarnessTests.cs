using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Foundatio.Messaging.Transport;
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
    public async Task WaitForIdleAsync_WhileARetryIsHeldInProcess_WaitsForTheRetry()
    {
        int attempts = 0;
        var retries = new RetryPolicy { MaxAttempts = 2, Delay = RetryPolicy.Fixed(TimeSpan.FromMilliseconds(300)) };
        var host = await StartHarnessAsync(o => o.TransportCapabilities = new TransportCapabilities { MaxRetryDelay = TimeSpan.Zero },
            services => services.AddFoundatioMessaging(m => m.AddConsumer<ShipOrder>((_, context, _) =>
            {
                Interlocked.Increment(ref attempts);
                return context.Attempt == 1 ? throw new InvalidOperationException("transient") : Task.CompletedTask;
            }, new HandlerOptions { RetryPolicy = retries })));
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();

        await host.Services.GetRequiredService<IMessageClient>().SendAsync(new ShipOrder(7), cancellationToken: TestCancellationToken);
        await harness.WaitForIdleAsync(cancellationToken: TestCancellationToken);

        Assert.Equal(2, Volatile.Read(ref attempts));
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
    public async Task HandledMessages_WhenTheHandlerReportsProgress_RecordsTheLastPercentAndMessage()
    {
        var host = await StartAppAsync(services => services.AddFoundatioMessaging(m => m.AddConsumer<ImportCatalog>(async (_, context, ct) =>
        {
            await context.ReportProgressAsync(50, "importing", ct);
            await context.ReportProgressAsync(100, cancellationToken: ct);
        })));
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();

        await host.Services.GetRequiredService<IMessageClient>().SendAsync(new ImportCatalog("spring"), cancellationToken: TestCancellationToken);
        var handled = Assert.Single(await harness.WaitForHandledAsync<ImportCatalog>(cancellationToken: TestCancellationToken));

        Assert.Equal(100, handled.Progress);
        Assert.Equal("importing", handled.ProgressMessage);
    }

    [Fact]
    public async Task JobRuns_WhenTheJobReportsProgress_RecordsTheLastPercentAndMessage()
    {
        var host = await StartAppAsync();
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();

        await harness.TriggerCronJobAsync<NightlyCleanupJob>(TestCancellationToken);
        var run = Assert.Single(await harness.WaitForJobAsync<NightlyCleanupJob>(cancellationToken: TestCancellationToken));

        Assert.Equal(100, run.Progress);
        Assert.Equal("cleaned", run.Message);
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

    [Fact]
    public async Task WaitForJobAsyncAndTriggerCronJobAsync_WithLegacyJobs_FindTheirRuns()
    {
        var host = await StartAppAsync(services => services.AddFoundatioJobs(j => j
            .AddLegacyJob<LegacyReportJob>()
            .AddCronJob<LegacyJob<LegacyArchiveJob>>("0 4 * * *")));
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();

        await host.Services.GetRequiredService<IJobClient>().EnqueueAsync<LegacyJob<LegacyReportJob>>(cancellationToken: TestCancellationToken);
        var report = Assert.Single(await harness.WaitForJobAsync<LegacyJob<LegacyReportJob>>(cancellationToken: TestCancellationToken));
        var handle = await harness.TriggerCronJobAsync<LegacyArchiveJob>(TestCancellationToken);
        var archive = Assert.Single(await harness.WaitForJobAsync<LegacyJob<LegacyArchiveJob>>(cancellationToken: TestCancellationToken));

        Assert.Same(report, Assert.Single(await harness.WaitForJobAsync<LegacyReportJob>(cancellationToken: TestCancellationToken)));
        Assert.Equal(JobState.Succeeded, report.State);
        Assert.Equal("tests.legacy-report", report.JobName);
        Assert.Equal("reported", report.Message);
        Assert.Equal(handle.Id, archive.JobId);
        Assert.Equal("tests.legacy-archive", archive.ScheduleName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.TriggerCronJobAsync<LegacyJob<LegacyReportJob>>(TestCancellationToken));
    }

    [Fact]
    public async Task AddFoundatioTestHarness_WithTransportCapabilities_DeliversWhatTheProductionTransportWould()
    {
        var withoutDeduplication = await StartHarnessAsync(o => o.TransportCapabilities = new TransportCapabilities { OrderedGroups = true });
        var deduplicating = await StartHarnessAsync();

        foreach (var host in new[] { withoutDeduplication, deduplicating })
        {
            var messages = host.Services.GetRequiredService<IMessageClient>();
            await messages.SendAsync(new SendReceipt(1, "a@b.com"), new SendOptions { DeduplicationId = "receipt-1" }, TestCancellationToken);
            await messages.SendAsync(new SendReceipt(1, "a@b.com"), new SendOptions { DeduplicationId = "receipt-1" }, TestCancellationToken);
            await host.Services.GetRequiredService<FoundatioTestHarness>().WaitForIdleAsync(cancellationToken: TestCancellationToken);
        }

        Assert.Equal(2, withoutDeduplication.Services.GetRequiredService<FoundatioTestHarness>().HandledMessages.Count(m => m.Message is SendReceipt));
        Assert.Equal(1, deduplicating.Services.GetRequiredService<FoundatioTestHarness>().HandledMessages.Count(m => m.Message is SendReceipt));
    }

    [Fact]
    public async Task AddFoundatioTestHarness_BeforeTheAppsRegistrations_StillOwnsTheTransportAndStore()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders().AddTestLogger(_output);
        builder.Services.AddFoundatioTestHarness();
        builder.Services.AddOrdersApp();
        builder.Services.AddFoundatioMessaging(m => m.UseInMemory().AddLegacyMessageBus());
        builder.Services.AddFoundatioJobs(j => j.UseInMemory().UseStore(_ => throw new InvalidOperationException("Tests must not reach this store.")));
        var host = builder.Build();
        _hosts.Add(host);
        await host.StartAsync(TestCancellationToken);
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();

        await host.Services.GetRequiredService<IMessageClient>().PublishAsync(new OrderPlaced(1, "a@b.com"), cancellationToken: TestCancellationToken);
        await harness.WaitForIdleAsync(cancellationToken: TestCancellationToken);

        Assert.Equal(["a@b.com: Receipt for order 1"], host.Services.GetRequiredService<EmailSender>().Sent);
        Assert.Equal(JobState.Succeeded, Assert.Single(harness.JobRuns).State);
        Assert.IsType<InMemoryMessageTransport>(host.Services.GetRequiredService<IMessageTransport>());
        Assert.IsType<Foundatio.Jobs.Store.InMemoryJobStore>(host.Services.GetRequiredService<Foundatio.Jobs.Store.IJobStore>());
    }

    [Fact]
    public async Task ProcessPendingAsync_WhenStartedPaused_RunsEachStepOnlyWhenAsked()
    {
        var host = await StartHarnessAsync(o => o.StartPaused = true);
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();
        var messages = host.Services.GetRequiredService<IMessageClient>();

        await messages.PublishAsync(new OrderPlaced(1, "a@b.com"), cancellationToken: TestCancellationToken);
        Assert.True(harness.IsPaused);
        Assert.Empty(harness.HandledMessages);

        var placed = await harness.ProcessPendingAsync<OrderPlaced>(cancellationToken: TestCancellationToken);
        Assert.Equal(MessageOutcome.Completed, Assert.Single(placed.HandledMessages).Outcome);
        Assert.Empty(placed.JobRuns);
        Assert.Single(harness.Sent<SendReceipt>());
        Assert.Empty(host.Services.GetRequiredService<EmailSender>().Sent);
        Assert.Equal(1, await host.Services.GetRequiredService<IJobClient>().CountAsync(JobState.Queued, cancellationToken: TestCancellationToken));

        var receipt = await harness.ProcessPendingAsync<SendReceipt>(cancellationToken: TestCancellationToken);
        Assert.IsType<SendReceipt>(Assert.Single(receipt.HandledMessages).Message);
        Assert.Equal(["a@b.com: Receipt for order 1"], host.Services.GetRequiredService<EmailSender>().Sent);
        Assert.Empty(harness.JobRuns);

        var rest = await harness.ProcessPendingAsync(cancellationToken: TestCancellationToken);
        Assert.Empty(rest.HandledMessages);
        Assert.Equal(JobState.Succeeded, Assert.Single(rest.JobRuns).State);
        Assert.True(harness.IsPaused);
    }

    [Fact]
    public async Task ProcessPendingAsync_WithARetryDelayOfZero_RunsRetriesAndTheDeadLetterInOneCall()
    {
        var host = await StartHarnessAsync(o =>
        {
            o.StartPaused = true;
            o.RetryDelay = TimeSpan.Zero;
        });
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();

        await host.Services.GetRequiredService<IMessageClient>().SendAsync(new SendReceipt(2, "bounce@b.com"), cancellationToken: TestCancellationToken);
        var processed = await harness.ProcessPendingAsync<SendReceipt>(cancellationToken: TestCancellationToken);

        Assert.Equal([MessageOutcome.Retried, MessageOutcome.Retried, MessageOutcome.Retried, MessageOutcome.Retried, MessageOutcome.DeadLettered], processed.HandledMessages.Select(m => m.Outcome));
        Assert.Equal([1, 2, 3, 4, 5], processed.HandledMessages.Select(m => m.Attempt));
    }

    [Fact]
    public async Task PauseProcessingAsync_HoldsNewWorkUntilResumed_AndKeepsInstanceSubscriptionsReceiving()
    {
        var host = await StartAppAsync();
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();
        var messages = host.Services.GetRequiredService<IMessageClient>();

        await harness.PauseProcessingAsync(cancellationToken: TestCancellationToken);
        await messages.SendAsync(new SendReceipt(3, "c@d.com"), cancellationToken: TestCancellationToken);
        await messages.PublishAsync(new PriceChanged("sku-3"), cancellationToken: TestCancellationToken);
        await host.Services.GetRequiredService<IJobClient>().EnqueueAsync(new GenerateInvoice(3), cancellationToken: TestCancellationToken);
        await Task.Delay(200, TestCancellationToken);
        Assert.Empty(harness.HandledMessages);
        Assert.Empty(harness.JobRuns);

        var price = await harness.ProcessPendingAsync<PriceChanged>(cancellationToken: TestCancellationToken);
        Assert.Equal(["sku-3"], host.Services.GetRequiredService<PriceCache>().Invalidated);
        Assert.StartsWith("instance:catalog.price-changed/", Assert.Single(price.HandledMessages).Address);

        harness.ResumeProcessing();
        await harness.WaitForIdleAsync(cancellationToken: TestCancellationToken);
        Assert.False(harness.IsPaused);
        Assert.Equal(["c@d.com: Receipt for order 3"], host.Services.GetRequiredService<EmailSender>().Sent);
        Assert.Equal(JobState.Succeeded, Assert.Single(harness.JobRuns).State);
    }

    [Fact]
    public async Task ProcessNextAsync_OfAHandlerThatSendsBackToItsOwnQueue_RunsOneDeliveryPerCall()
    {
        var host = await StartHarnessAsync(o => o.StartPaused = true, s => s.AddFoundatioMessaging(m => m.AddConsumer<ImportBatch>(async (batch, context, ct) =>
        {
            if (batch.Items.Length > 1)
            {
                foreach (int item in batch.Items)
                    await context.Services.GetRequiredService<IMessageClient>().SendAsync(new ImportBatch([item]), cancellationToken: ct);
            }
        })));
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();
        await host.Services.GetRequiredService<IMessageClient>().SendAsync(new ImportBatch([1, 2]), cancellationToken: TestCancellationToken);

        var split = await harness.ProcessNextAsync<ImportBatch>(cancellationToken: TestCancellationToken);
        Assert.Equal([1, 2], Assert.IsType<ImportBatch>(Assert.Single(split.HandledMessages).Message).Items);
        Assert.Equal(3, harness.Sent<ImportBatch>().Count);
        Assert.Single(harness.HandledMessages);

        var first = await harness.ProcessNextAsync<ImportBatch>(cancellationToken: TestCancellationToken);
        var second = await harness.ProcessNextAsync<ImportBatch>(cancellationToken: TestCancellationToken);
        var none = await harness.ProcessNextAsync<ImportBatch>(cancellationToken: TestCancellationToken);

        Assert.Equal([1], Assert.IsType<ImportBatch>(Assert.Single(first.HandledMessages).Message).Items);
        Assert.Equal([2], Assert.IsType<ImportBatch>(Assert.Single(second.HandledMessages).Message).Items);
        Assert.Empty(none.HandledMessages);
        Assert.All(harness.HandledMessages, m => Assert.Equal(MessageOutcome.Completed, m.Outcome));
        Assert.True(harness.IsPaused);
    }

    [Fact]
    public async Task ProcessNextAsync_WithARetryDelayOfZero_RunsEachRetryAsItsOwnStep()
    {
        var host = await StartHarnessAsync(o =>
        {
            o.StartPaused = true;
            o.RetryDelay = TimeSpan.Zero;
        });
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();
        await host.Services.GetRequiredService<IMessageClient>().SendAsync(new SendReceipt(2, "bounce@b.com"), cancellationToken: TestCancellationToken);

        for (int attempt = 1; attempt <= 4; attempt++)
        {
            var step = Assert.Single((await harness.ProcessNextAsync<SendReceipt>(cancellationToken: TestCancellationToken)).HandledMessages);
            Assert.Equal(MessageOutcome.Retried, step.Outcome);
            Assert.Equal(attempt, step.Attempt);
        }

        var last = Assert.Single((await harness.ProcessNextAsync<SendReceipt>(cancellationToken: TestCancellationToken)).HandledMessages);
        Assert.Equal(MessageOutcome.DeadLettered, last.Outcome);
        Assert.Equal(5, last.Attempt);
    }

    [Fact]
    public async Task ProcessNextAsync_WithACount_RunsThatManyAndLeavesTheRest()
    {
        var host = await StartHarnessAsync(o => o.StartPaused = true);
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();
        var messages = host.Services.GetRequiredService<IMessageClient>();
        var jobs = host.Services.GetRequiredService<IJobClient>();
        for (int i = 1; i <= 3; i++)
        {
            await messages.SendAsync(new SendReceipt(i, "a@b.com"), cancellationToken: TestCancellationToken);
            await jobs.EnqueueAsync(new GenerateInvoice(i), cancellationToken: TestCancellationToken);
        }

        var receipts = await harness.ProcessNextAsync<SendReceipt>(2, cancellationToken: TestCancellationToken);
        var invoice = await harness.ProcessNextAsync<GenerateInvoice>(cancellationToken: TestCancellationToken);
        var anyTwo = await harness.ProcessNextAsync(2, cancellationToken: TestCancellationToken);
        var rest = await harness.ProcessPendingAsync(cancellationToken: TestCancellationToken);

        Assert.Equal(2, receipts.HandledMessages.Count);
        Assert.Empty(receipts.JobRuns);
        Assert.Equal(JobState.Succeeded, Assert.Single(invoice.JobRuns).State);
        Assert.Empty(invoice.HandledMessages);
        Assert.Equal(2, anyTwo.HandledMessages.Count + anyTwo.JobRuns.Count);
        Assert.Equal(1, rest.HandledMessages.Count + rest.JobRuns.Count);
        Assert.Equal(3, harness.HandledMessages.Count);
        Assert.Equal(3, harness.JobRuns.Count);
    }

    [Fact]
    public async Task ProcessNextAsync_WithTheLegacyMessageBus_SpendsItsStepsOnlyOnTheAppsHandlers()
    {
        var host = await StartHarnessAsync(o => o.StartPaused = true, s => s.AddFoundatioMessaging(m => m.AddLegacyMessageBus()));
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();
        var bus = host.Services.GetRequiredService<Foundatio.Messaging.IMessageBus>();
        int legacy = 0;
        await bus.SubscribeAsync<Bounce>(_ => Interlocked.Increment(ref legacy), TestCancellationToken);

        await bus.PublishAsync(new Bounce(1), cancellationToken: TestCancellationToken);
        await host.Services.GetRequiredService<IMessageClient>().SendAsync(new SendReceipt(1, "a@b.com"), cancellationToken: TestCancellationToken);
        var step = await harness.ProcessNextAsync(cancellationToken: TestCancellationToken);

        Assert.IsType<SendReceipt>(Assert.Single(step.HandledMessages).Message);
        Assert.Equal(["a@b.com: Receipt for order 1"], host.Services.GetRequiredService<EmailSender>().Sent);
        Assert.Equal(1, Volatile.Read(ref legacy));
    }

    [Theory]
    [InlineData(false, "idle")]
    [InlineData(true, "idle")]
    [InlineData(true, "pending")]
    [InlineData(true, "next")]
    public async Task Waits_AfterALegacyBusPublish_WaitForTheLegacySubscriber(bool paused, string wait)
    {
        var host = await StartHarnessAsync(o => o.StartPaused = paused, s => s.AddFoundatioMessaging(m => m.AddLegacyMessageBus()));
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();
        var bus = host.Services.GetRequiredService<Foundatio.Messaging.IMessageBus>();
        int handled = 0;
        await bus.SubscribeAsync<Bounce>(async (_, ct) =>
        {
            await Task.Delay(200, ct);
            Interlocked.Increment(ref handled);
        }, TestCancellationToken);

        await bus.PublishAsync(new Bounce(1), cancellationToken: TestCancellationToken);
        if (wait == "next")
            await host.Services.GetRequiredService<IMessageClient>().SendAsync(new SendReceipt(1, "a@b.com"), cancellationToken: TestCancellationToken);

        var processed = wait switch
        {
            "pending" => await harness.ProcessPendingAsync(TimeSpan.FromSeconds(10), TestCancellationToken),
            "next" => await harness.ProcessNextAsync(1, TimeSpan.FromSeconds(10), TestCancellationToken),
            _ => null
        };
        if (processed is null)
            await harness.WaitForIdleAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        Assert.Equal(1, Volatile.Read(ref handled));
        Assert.Equal(paused, harness.IsPaused);
        if (wait == "next")
            Assert.IsType<SendReceipt>(Assert.Single(processed!.HandledMessages).Message);
    }

    [Fact]
    public async Task ProcessNextAsync_WithOrderedGroupsAndFailures_RunsExactlyTheCountOfHandlers()
    {
        for (int round = 0; round < 5; round++)
        {
            int started = 0;
            var host = await StartHarnessAsync(o =>
            {
                o.StartPaused = true;
                o.RetryDelay = TimeSpan.Zero;
                o.TransportCapabilities = new TransportCapabilities { OrderedGroups = true, MaxReceiveBatchSize = 10 };
            }, s => s.AddFoundatioMessaging(m => m.AddConsumer<ImportBatch>(async (batch, _, ct) =>
            {
                Interlocked.Increment(ref started);
                await Task.Delay(Random.Shared.Next(0, 5), ct);
                if (batch.Items[0] % 4 == 0)
                    throw new InvalidOperationException("Import failed");
            }, new HandlerOptions { MaxConcurrency = 8, RetryPolicy = new RetryPolicy { MaxAttempts = 3 } })));
            var harness = host.Services.GetRequiredService<FoundatioTestHarness>();
            var messages = host.Services.GetRequiredService<IMessageClient>();
            for (int i = 0; i < 12; i++)
                await messages.SendAsync(new ImportBatch([i]), new SendOptions { GroupKey = $"g{i % 3}" }, TestCancellationToken);

            int total = 0;
            foreach (int count in new[] { 1, 3, 2, 5, 1, 4 })
            {
                var step = await harness.ProcessNextAsync<ImportBatch>(count, TimeSpan.FromSeconds(10), TestCancellationToken);
                Assert.True(step.HandledMessages.Count == count, $"Round {round}: ProcessNextAsync({count}) recorded "
                    + String.Join(", ", step.HandledMessages.Select(m => $"{m.Message} {m.Outcome}")));
                total += count;
            }

            Assert.Equal(total, Volatile.Read(ref started));
            Assert.Equal(total, harness.HandledMessages.Count);
            await host.StopAsync(TestCancellationToken);
        }
    }

    [Fact]
    public async Task ProcessPendingAsync_WithARetryDelayOfZero_AlsoShortensAHandlersOwnRetryDelay()
    {
        var host = await StartHarnessAsync(o =>
        {
            o.StartPaused = true;
            o.RetryDelay = TimeSpan.Zero;
        }, s => s.AddFoundatioMessaging(m => m.AddConsumer<ImportBatch>((_, context, _) =>
        {
            if (context.Attempt == 1)
                context.Retry(TimeSpan.FromMinutes(5));

            return Task.CompletedTask;
        })));
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();
        await host.Services.GetRequiredService<IMessageClient>().SendAsync(new ImportBatch([1]), cancellationToken: TestCancellationToken);

        var processed = await harness.ProcessPendingAsync(TimeSpan.FromSeconds(10), TestCancellationToken);

        Assert.Equal([MessageOutcome.Retried, MessageOutcome.Completed], processed.HandledMessages.Select(m => m.Outcome));
    }

    [Fact]
    public async Task MessageAdmin_WhilePaused_PeeksAndReplaysDeadLetters()
    {
        var host = await StartHarnessAsync(o =>
        {
            o.StartPaused = true;
            o.RetryDelay = TimeSpan.Zero;
        });
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();
        var admin = host.Services.GetRequiredService<IMessageAdmin>();
        var queue = admin.GetQueue<SendReceipt>();
        await host.Services.GetRequiredService<IMessageClient>().SendAsync(new SendReceipt(7, "bounce@b.com"), cancellationToken: TestCancellationToken);
        await harness.ProcessPendingAsync(cancellationToken: TestCancellationToken);

        var letter = Assert.Single(await admin.PeekDeadLettersAsync(queue, cancellationToken: TestCancellationToken));
        Assert.True(await admin.ReplayDeadLetterAsync(queue, letter.MessageId, TestCancellationToken));
        Assert.Empty(await admin.PeekDeadLettersAsync(queue, cancellationToken: TestCancellationToken));
        Assert.True(harness.IsPaused);

        var replayed = await harness.ProcessPendingAsync(cancellationToken: TestCancellationToken);
        Assert.Equal(MessageOutcome.DeadLettered, replayed.HandledMessages.Last().Outcome);
        Assert.All(replayed.HandledMessages, m => Assert.Equal(7, Assert.IsType<SendReceipt>(m.Message).OrderId));
    }

    [Fact]
    public async Task ProcessNextAsync_WhenNotPaused_Throws()
    {
        var host = await StartHarnessAsync();
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.ProcessNextAsync<SendReceipt>(cancellationToken: TestCancellationToken));

        Assert.Contains("PauseProcessingAsync", error.Message);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.ProcessNextAsync(0, cancellationToken: TestCancellationToken));
    }

    [Fact]
    public async Task WaitForIdleAsync_WhilePaused_RunsAllTheWorkThenStaysPaused()
    {
        var host = await StartHarnessAsync(o => o.StartPaused = true);
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();
        var messages = host.Services.GetRequiredService<IMessageClient>();

        await messages.PublishAsync(new OrderPlaced(1, "a@b.com"), cancellationToken: TestCancellationToken);
        await harness.WaitForIdleAsync(cancellationToken: TestCancellationToken);

        Assert.True(harness.IsPaused);
        Assert.Equal(["a@b.com: Receipt for order 1"], host.Services.GetRequiredService<EmailSender>().Sent);
        Assert.Equal(JobState.Succeeded, Assert.Single(harness.JobRuns).State);

        await messages.SendAsync(new SendReceipt(2, "c@d.com"), cancellationToken: TestCancellationToken);
        await Task.Delay(200, TestCancellationToken);
        Assert.Single(host.Services.GetRequiredService<EmailSender>().Sent);
        Assert.Single((await harness.ProcessNextAsync<SendReceipt>(cancellationToken: TestCancellationToken)).HandledMessages);
    }

    [Fact]
    public async Task ProcessPendingAsync_OfATypeNothingHandles_Throws()
    {
        var host = await StartHarnessAsync(o => o.StartPaused = true);
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.ProcessPendingAsync<ShipOrder>(cancellationToken: TestCancellationToken));

        Assert.Contains("Nothing in this app handles or runs", error.Message);
    }

    [Fact]
    public async Task ProcessPendingAsync_WithAFakeClock_LeavesDelayedWorkUntilTheClockReachesIt()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var host = await StartHarnessAsync(o =>
        {
            o.StartPaused = true;
            o.TimeProvider = clock;
        });
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();

        await host.Services.GetRequiredService<IMessageClient>().SendAsync(new SendReceipt(4, "e@f.com"), new SendOptions { Delay = TimeSpan.FromHours(1) }, TestCancellationToken);
        Assert.Empty((await harness.ProcessPendingAsync(cancellationToken: TestCancellationToken)).HandledMessages);

        clock.Advance(TimeSpan.FromHours(1));
        var processed = await harness.ProcessPendingAsync(cancellationToken: TestCancellationToken);

        Assert.Equal(MessageOutcome.Completed, Assert.Single(processed.HandledMessages).Outcome);
    }

    [Fact]
    public async Task Reset_ForgetsWhatWasRecorded_SoWaitsCountFromThere()
    {
        var host = await StartAppAsync();
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();
        var messages = host.Services.GetRequiredService<IMessageClient>();
        await messages.PublishAsync(new OrderPlaced(5, "a@b.com"), cancellationToken: TestCancellationToken);
        await harness.WaitForIdleAsync(cancellationToken: TestCancellationToken);

        harness.Reset();

        Assert.Empty(harness.SentMessages);
        Assert.Empty(harness.PublishedMessages);
        Assert.Empty(harness.HandledMessages);
        Assert.Empty(harness.JobRuns);
        await Assert.ThrowsAsync<TimeoutException>(() => harness.WaitForJobAsync<GenerateInvoice>(timeout: TimeSpan.FromMilliseconds(100), cancellationToken: TestCancellationToken));
        await messages.SendAsync(new SendReceipt(6, "c@d.com"), cancellationToken: TestCancellationToken);
        Assert.Single(await harness.WaitForHandledAsync<SendReceipt>(cancellationToken: TestCancellationToken));
    }

    [Fact]
    public async Task WaitForHandledByAsync_TellsApartHandlersThatShareASubscription()
    {
        var host = await StartAppAsync(services => services.AddSingleton<AuditLog>().AddFoundatioMessaging(m => m.AddSubscriber<OrderAuditor>()));
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();

        await host.Services.GetRequiredService<IMessageClient>().PublishAsync(new OrderPlaced(7, "a@b.com"), cancellationToken: TestCancellationToken);
        var audited = Assert.Single(await harness.WaitForHandledByAsync<OrderAuditor>(cancellationToken: TestCancellationToken));

        Assert.Equal([nameof(OrderPlacedHandler), nameof(OrderAuditor)], audited.Handlers);
        Assert.Same(audited, Assert.Single(harness.HandledBy<OrderPlacedHandler>()));
        Assert.Empty(harness.HandledBy<PriceCacheInvalidator>());
        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => harness.WaitForHandledByAsync<OrderAuditor>(2, TimeSpan.FromMilliseconds(100), TestCancellationToken));
        Assert.Contains("OrderAuditor to handle 2 message(s); it handled 1", timeout.Message);
    }

    [Fact]
    public async Task WaitForHandledAsync_OfAnEventThatWasSent_SuggestsPublishing()
    {
        var host = await StartAppAsync();
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();

        await host.Services.GetRequiredService<IMessageClient>().SendAsync(new OrderPlaced(8, "a@b.com"), cancellationToken: TestCancellationToken);
        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => harness.WaitForHandledAsync<OrderPlaced>(timeout: TimeSpan.FromMilliseconds(100), cancellationToken: TestCancellationToken));

        Assert.Contains("Did you mean PublishAsync?", timeout.Message);
    }

    [Fact]
    public async Task AddConsumer_WithOptionsFromServices_AndAHarnessRetryDelay_RetriesWithoutTheHandlersOwnBackoff()
    {
        var host = await StartHarnessAsync(o => o.RetryDelay = TimeSpan.FromMilliseconds(10), services => services
            .AddSingleton(new ReceiptSettings(Attempts: 2))
            .AddFoundatioMessaging(m => m.AddConsumer<BouncingHandler>(sp => new HandlerOptions
            {
                RetryPolicy = new RetryPolicy { MaxAttempts = sp.GetRequiredService<ReceiptSettings>().Attempts, Delay = RetryPolicy.Fixed(TimeSpan.FromMinutes(10)) }
            })));
        var harness = host.Services.GetRequiredService<FoundatioTestHarness>();

        await host.Services.GetRequiredService<IMessageClient>().SendAsync(new Bounce(1), cancellationToken: TestCancellationToken);
        var deadLetter = Assert.Single(await harness.WaitForDeadLetteredAsync<Bounce>(timeout: TimeSpan.FromSeconds(10), cancellationToken: TestCancellationToken));

        Assert.Equal(2, deadLetter.Attempt);
    }

    [Fact]
    public async Task AddFoundatioTestHarness_WithASharedTransportAndStore_RecordsEachHostsShareOfTheWork()
    {
        await using var transport = new InMemoryMessageTransport();
        var store = new Foundatio.Jobs.Store.InMemoryJobStore();
        var pods = new List<IHost>();
        for (int i = 0; i < 3; i++)
            pods.Add(await StartHarnessAsync(o => { o.Transport = transport; o.JobStore = store; }));
        var harnesses = pods.Select(p => p.Services.GetRequiredService<FoundatioTestHarness>()).ToList();

        await pods[0].Services.GetRequiredService<IMessageClient>().PublishAsync(new OrderPlaced(9, "a@b.com"), cancellationToken: TestCancellationToken);
        await pods[1].Services.GetRequiredService<IMessageClient>().PublishAsync(new PriceChanged("sku-9"), cancellationToken: TestCancellationToken);
        foreach (var harness in harnesses)
            await harness.WaitForIdleAsync(cancellationToken: TestCancellationToken);

        Assert.Equal(1, harnesses.Sum(h => h.HandledMessages.Count(m => m.Message is OrderPlaced)));
        Assert.Equal(1, harnesses.Sum(h => h.JobRuns.Count));
        Assert.All(harnesses, h => Assert.Single(h.HandledMessages, m => m.Message is PriceChanged));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harnesses[0].PauseProcessingAsync(cancellationToken: TestCancellationToken));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddFoundatioTestHarness(o => { o.Transport = transport; o.StartPaused = true; }));
    }

    [Fact]
    public void AddFoundatioTestHarness_WithCapabilitiesTheInMemoryTransportCannotEmulate_Throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddFoundatioTestHarness(o => o.TransportCapabilities = new TransportCapabilities { MaxReceiveBatchSize = 0 }));
    }

    private async Task<IHost> StartHarnessAsync(Action<FoundatioTestHarnessOptions>? configure = null, Action<IServiceCollection>? configureTest = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders().AddTestLogger(_output);
        builder.Services.AddOrdersApp();
        configureTest?.Invoke(builder.Services);
        builder.Services.AddFoundatioTestHarness(configure);
        var host = builder.Build();
        _hosts.Add(host);
        await host.StartAsync(TestCancellationToken);
        return host;
    }

    public record ReceiptSettings(int Attempts);

    [Message("tests.import-batch")]
    public record ImportBatch(int[] Items);

    public record Bounce(int Id);

    public sealed class BouncingHandler : IMessageHandler<Bounce>
    {
        public Task HandleAsync(Bounce message, IMessageContext context, CancellationToken cancellationToken) => throw new InvalidOperationException("Bounced");
    }

    public sealed class AuditLog
    {
        public System.Collections.Concurrent.ConcurrentQueue<int> Orders { get; } = new();
    }

    public sealed class OrderAuditor(AuditLog log) : IMessageHandler<OrderPlaced>
    {
        public Task HandleAsync(OrderPlaced message, IMessageContext context, CancellationToken cancellationToken)
        {
            log.Orders.Enqueue(message.OrderId);
            return Task.CompletedTask;
        }
    }

    public record ArchiveOrders(int OlderThanDays);

    public record ImportCatalog(string Season);

    [Job(Name = "tests.legacy-report")]
    public sealed class LegacyReportJob : IJob
    {
        public Task<JobResult> RunAsync(CancellationToken cancellationToken = default) => Task.FromResult(JobResult.SuccessWithMessage("reported"));
    }

    [Job(Name = "tests.legacy-archive")]
    public sealed class LegacyArchiveJob : IJob
    {
        public Task<JobResult> RunAsync(CancellationToken cancellationToken = default) => Task.FromResult(JobResult.Success);
    }

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
