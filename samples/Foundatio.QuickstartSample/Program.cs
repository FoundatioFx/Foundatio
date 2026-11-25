// Foundatio quickstart: messaging and durable jobs, entirely in memory. Run it with `dotnet run`, then:
//   curl -X POST localhost:5000/orders                  publish an event; a subscriber sends a command
//   curl -X POST localhost:5000/reports                 enqueue a durable job with arguments
//   curl localhost:5000/reports/{id}                    poll the job's status and progress
//   curl localhost:5000/monitor                         what messages and jobs are doing: active, recent, stats, backlog
// `dotnet run -- --verify` runs the same flow once and exits (used in CI).
// To run several instances, add Foundatio.Redis, register an IConnectionMultiplexer and replace both UseInMemory() calls
// with UseRedis(); register a RedisCacheClient as ICacheClient and call UseCache() on AddFoundatioMonitoring so the
// monitor shows every instance. Handlers and jobs do not change.
using Foundatio.Jobs;
using Foundatio.Messaging;
using Foundatio.Monitoring;
using Foundatio.QuickstartSample;

bool verify = args.Contains("--verify");
var builder = WebApplication.CreateBuilder(args.Where(a => a != "--verify").ToArray());
builder.Services.AddSingleton<SampleActivity>();

builder.Services.AddFoundatioMessaging(m => m
    .UseServiceName("quickstart")
    .UseInMemory()
    .AddSubscriber<OrderPlacedHandler>()
    .AddConsumer<SendReceiptHandler>());

builder.Services.AddFoundatioJobs(j => j
    .UseInMemory()
    .AddJob<GenerateReportJob>()
    .AddCronJob<CleanupJob>("*/2 * * * * *"));

builder.Services.AddFoundatioMonitoring();

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
        ? Results.Ok(new { job.Id, State = job.State.ToString(), job.Progress, job.Message, job.Error })
        : Results.NotFound());

app.MapGet("/monitor", (IWorkMonitor monitor, CancellationToken cancellationToken) => monitor.GetOverviewAsync(cancellationToken: cancellationToken));

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
if (completed.State != JobState.Succeeded || completed.Progress != 100)
    throw new InvalidOperationException($"Report finished as {completed.State} at {completed.Progress}%.");

await activity.CleanupRan.Task.WaitAsync(TimeSpan.FromSeconds(10));

var overview = await app.Services.GetRequiredService<IWorkMonitor>().GetOverviewAsync();
var receipts = overview.Stats.SingleOrDefault(s => s.Name == "orders.send-receipt");
var receipt = overview.Recent.FirstOrDefault(r => r.Name == "orders.send-receipt");
var reports = overview.Jobs.SingleOrDefault(j => j.Name == "reports.generate");
if (overview.Instances.Count != 1 || receipts?.Succeeded != 1 || receipt is not { Outcome: WorkOutcome.Succeeded, Progress: 100 }
    || reports?.Succeeded != 1 || !overview.Backlog.Any(b => b.Source.Name == "orders.send-receipt"))
{
    throw new InvalidOperationException($"The monitor did not show the flow: {receipts} {receipt} {reports}.");
}

Console.WriteLine($"Verified: event -> command, job {completed.Id} -> {completed.Message}, CRON cleanup ran, monitor saw {receipts.Succeeded} receipt ({receipt.ProgressMessage}).");
await app.StopAsync();
