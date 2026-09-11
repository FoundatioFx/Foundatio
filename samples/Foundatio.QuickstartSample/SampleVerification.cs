using Foundatio.Jobs;
using Foundatio.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Foundatio.QuickstartSample;

internal static class SampleVerification
{
    public static async Task RunAsync(IHost host, JobState completed)
    {
        if (completed.Status != JobStatus.Completed || completed.Error is not null || completed.ResultMessage is null)
            throw new InvalidOperationException("The delayed job did not complete successfully.");

        var activity = host.Services.GetRequiredService<SampleActivity>();
        await Task.WhenAll(activity.EventHandled.Task, activity.CommandHandled.Task, activity.CleanupRan.Task)
            .WaitAsync(TimeSpan.FromSeconds(70));

        var jobs = host.Services.GetRequiredService<IJobClient>();
        var cancelled = await jobs.EnqueueAsync<ResizeImageJob, ResizeArgs>(new ResizeArgs("cancelled.png", 32, 32),
            new JobRequestOptions { Delay = TimeSpan.FromHours(1) });
        await cancelled.RequestCancellationAsync();
        if ((await cancelled.WaitForCompletionAsync(TimeSpan.FromSeconds(5))).Status != JobStatus.Cancelled)
            throw new InvalidOperationException("Cancellation was not persisted.");

        var services = new ServiceCollection();
        services.AddFoundatio().ConfigureMessaging(messaging => messaging.UseInMemory()
            .AddMessageType<SendReceipt>("send-receipt.v1", queue: "receipts"));
        await using var producer = services.BuildServiceProvider();
        if (producer.GetServices<IHostedService>().Any())
            throw new InvalidOperationException("Producer registration started a hosted service.");
        var bus = producer.GetRequiredService<IMessageBus>();
        await bus.SendAsync(new SendReceipt(2002, "producer@example.com"));
        await using var delivery = await bus.ReceiveAsync<SendReceipt>();
        if (delivery?.Message.OrderId != 2002)
            throw new InvalidOperationException("The producer-only message was not available.");
        await delivery.CompleteAsync();
        Console.WriteLine("Verified: producer-only registration, command, durable subscriber, delayed job, cancellation, and automatic CRON execution.");
    }
}
