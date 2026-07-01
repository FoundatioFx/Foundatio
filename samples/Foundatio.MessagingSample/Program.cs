using Foundatio;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Foundatio.MessagingSample;

var builder = WebApplication.CreateBuilder(args);

// A short id so log lines make it obvious WHICH instance handled each message/job when scaled to multiple replicas.
builder.Services.AddSingleton(new InstanceInfo(Guid.NewGuid().ToString("N")[..6]));

builder.Services.AddFoundatio()
    // Messaging on AWS (SQS/SNS). Handlers are registered declaratively; Foundatio hosts them and dispatches to them —
    // no hand-written IHostedService. Swap UseAws() for UseRedis() to run messaging on Redis Streams instead.
    .Messaging.UseAws()
    .Messaging.AddQueueHandler<ProcessOrder, ProcessOrderHandler>()           // competing consumers: one instance per order
    .Messaging.AddBroadcastHandler<Announcement, AnnouncementHandler>()       // fan-out: every instance gets each announcement
    // Durable jobs on Redis so any instance can claim them. The pump (auto-registered) runs submitted jobs and
    // materializes the CRON schedules below — no manual scheduling call.
    .Jobs.UseRedis()
    .Jobs.Register<GenerateReportJob>("generate-report")                      // on-demand, submitted via POST /reports
    .Jobs.AddCronJob<HeartbeatJob>("* * * * *")                               // Global: one instance per tick
    .Jobs.AddCronJob<RefreshCacheJob>("* * * * *", o => o.Scope = ScheduledJobScope.PerNode) // every instance per tick
    .Jobs.AddCronJob<SweepStaleOrdersJob>("*/2 * * * *");                     // Global: periodic sweep

var app = builder.Build();

app.MapGet("/", (InstanceInfo instance) => Results.Ok(new { service = "Foundatio messaging sample", instance = instance.Id }));

// QUEUE — competing consumers: exactly one instance processes each order (handled by ProcessOrderHandler).
app.MapPost("/orders", async (ProcessOrder order, IQueue queue) =>
    Results.Accepted(value: new { queued = await queue.EnqueueAsync(order) }));

// PUB/SUB — fan-out: every instance receives each announcement (handled by AnnouncementHandler).
app.MapPost("/announcements", async (Announcement announcement, IPubSub pubSub) =>
{
    await pubSub.PublishAsync(announcement);
    return Results.Accepted(value: new { published = announcement.Text });
});

// DURABLE JOB — submitted here, executed on whichever instance's runtime pump claims it.
app.MapPost("/reports", async (IJobClient jobs) =>
{
    var handle = await jobs.EnqueueAsync<GenerateReportJob>();
    return Results.Accepted($"/reports/{handle.JobId}", new { jobId = handle.JobId });
});

app.MapGet("/reports/{id}", async (string id, IJobMonitor monitor) =>
{
    var state = await monitor.GetAsync(id);
    return state is null
        ? Results.NotFound()
        : Results.Ok(new { state.JobId, status = state.Status.ToString(), state.Progress, state.ProgressMessage });
});

app.Run();
