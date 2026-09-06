// Foundatio quickstart: messaging + durable jobs with ZERO external dependencies (everything in-memory).
// Just `dotnet run` — publish an event, send a command, run a durable job with typed args, and watch a CRON
// job tick once a minute. Swap UseInMemory() for UseRedis()/UseAws() to go to production without touching
// any handler or job code.
using Foundatio;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Foundatio.QuickstartSample;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddFoundatioWorker(foundatio => foundatio
    // Register queued work and durable event subscriptions explicitly.
    .Messaging.UseInMemory()
    .Messaging.AddSubscriber<OrderPlaced, OrderPlacedHandler>("orders")
    .Messaging.AddConsumer<SendReceipt, SendReceiptHandler>()
    // Register the jobs this worker can execute.
    .Jobs.UseInMemory()
    .Jobs.AddJobType<ResizeImageJob>("resize-image")
    .Jobs.AddCronJob<CleanupJob>("*/1 * * * *")); // fires within a minute — watch for the CRON tick log line

var host = builder.Build();
await host.StartAsync(); // handlers attach and the job worker starts here

var bus = host.Services.GetRequiredService<IMessageBus>();
var jobs = host.Services.GetRequiredService<IJobClient>();

// EVENT — every subscribing service receives a copy (OrderPlacedHandler logs it).
await bus.PublishAsync(new OrderPlaced(1001, "Espresso Machine"));

// COMMAND — competing consumers process it (SendReceiptHandler logs it).
await bus.SendAsync(new SendReceipt(1001, "dev@example.com"));

// DURABLE JOB with typed arguments — a worker claims it, the job reads the args back and reports progress.
var handle = await jobs.EnqueueAsync<ResizeImageJob, ResizeArgs>(new ResizeArgs("product-1001.png", 640, 480));
Console.WriteLine($"Enqueued ResizeImageJob {handle.JobId}; CleanupJob (CRON) ticks within a minute. Ctrl+C to exit.");

await host.WaitForShutdownAsync(); // graceful shutdown on Ctrl+C
