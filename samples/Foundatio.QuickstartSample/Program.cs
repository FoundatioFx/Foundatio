// Foundatio quickstart: messaging and durable jobs, entirely in memory. Run it with `dotnet run`, then:
//   curl -X POST localhost:5000/orders                  publish an event; a subscriber sends a command
//   curl -X POST localhost:5000/reports                 enqueue a durable job with arguments
//   curl localhost:5000/reports/{id}                    poll the job's status and progress
// `dotnet run -- --verify` runs the same flow once and exits (used in CI).
// For production, replace UseInMemory() with a provider such as UseRedis(...). Handlers and jobs do not change.
using Foundatio.Jobs;
using Foundatio.Messaging;
using Foundatio.QuickstartSample;

bool verify = args.Contains("--verify");
var builder = WebApplication.CreateBuilder(args.Where(a => a != "--verify").ToArray());
builder.Services.AddSingleton<SampleActivity>();

builder.Services.AddFoundatioMessaging(m => m
    .UseInMemory()
    .AddSubscriber<OrderPlacedHandler>()
    .AddConsumer<SendReceiptHandler>());

builder.Services.AddFoundatioJobs(j => j
    .UseInMemory()
    .AddJob<GenerateReportJob>()
    .AddCronJob<CleanupJob>("*/2 * * * * *"));

var app = builder.Build();

app.MapPost("/orders", async (IMessageClient messages) =>
{
    var order = new OrderPlaced(Random.Shared.Next(1000, 9999), "dev@example.com", 42.50m);
    await messages.PublishAsync(order, new PublishOptions { CorrelationId = $"order-{order.OrderId}" });
    return Results.Accepted(value: order);
});

app.MapPost("/reports", async (IJobClient jobs) =>
{
    var job = await jobs.EnqueueAsync(new GenerateReport("pdf", Days: 5));
    return Results.Accepted($"/reports/{job.Id}", new { job.Id });
});

app.MapGet("/reports/{id}", async (string id, IJobClient jobs) =>
    await jobs.GetAsync(id) is { } job
        ? Results.Ok(new { job.Id, Status = job.Status.ToString(), job.Progress, job.Message, job.Error })
        : Results.NotFound());

if (!verify)
{
    app.Run();
    return;
}

await app.StartAsync();
var activity = app.Services.GetRequiredService<SampleActivity>();
await app.Services.GetRequiredService<IMessageClient>().PublishAsync(new OrderPlaced(1001, "dev@example.com", 42.50m), new PublishOptions { CorrelationId = "order-1001" });
await activity.ReceiptSent.Task.WaitAsync(TimeSpan.FromSeconds(10));

var report = await app.Services.GetRequiredService<IJobClient>().EnqueueAsync(new GenerateReport("pdf", Days: 3));
var completed = await report.WaitForCompletionAsync(TimeSpan.FromSeconds(10));
if (completed.Status != JobStatus.Succeeded || completed.Progress != 100)
    throw new InvalidOperationException($"Report finished as {completed.Status} at {completed.Progress}%.");

await activity.CleanupRan.Task.WaitAsync(TimeSpan.FromSeconds(10));
Console.WriteLine($"Verified: event -> command, job {completed.Id} -> {completed.Message}, CRON cleanup ran.");
await app.StopAsync();
