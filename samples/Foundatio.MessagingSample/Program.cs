using Foundatio;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Foundatio.MessagingSample;

var builder = WebApplication.CreateBuilder(args);

// A short id so log lines make it obvious WHICH instance handled each message/job when scaled to multiple replicas.
builder.Services.AddSingleton(new InstanceInfo(Guid.NewGuid().ToString("N")[..6]));

builder.Services.AddFoundatio()
    // Messaging on AWS (SQS/SNS). Handlers carry no topology decision — the caller's verb decides delivery
    // (bus.SendAsync = one instance across the fleet, bus.PublishAsync = once per subscribing service). Swap UseAws()
    // for UseRedis() to run messaging on Redis Streams without touching any handler.
    .Messaging.UseAws()
    .Messaging.AddHandler<ProcessOrder, ProcessOrderHandler>()
    .Messaging.AddHandler<Announcement, AnnouncementHandler>(o => o.PerInstance = true) // every replica shows the announcement
    // Durable jobs on Redis so any instance can claim them. The pump (auto-registered) runs submitted jobs and
    // materializes the CRON schedules below — no manual scheduling call.
    .Jobs.UseRedis()
    .Jobs.Register<GenerateReportJob>("generate-report")                      // on-demand, submitted via POST /reports
    .Jobs.AddCronJob<HeartbeatJob>("* * * * *")                               // Global: one instance per tick
    .Jobs.AddCronJob<RefreshCacheJob>("* * * * *", o => o.Scope = ScheduledJobScope.PerNode) // every instance per tick
    .Jobs.AddCronJob<SweepStaleOrdersJob>("*/2 * * * *");                     // Global: periodic sweep

var app = builder.Build();

app.MapGet("/", (InstanceInfo instance) => Results.Ok(new { service = "Foundatio messaging sample", instance = instance.Id }));

// SEND — a command / unit of work: exactly one instance processes each order (handled by ProcessOrderHandler).
app.MapPost("/orders", async (ProcessOrder order, IMessageBus bus) =>
    Results.Accepted(value: new { queued = await bus.SendAsync(order) }));

// PUBLISH — an event: subscribers receive it per their registration (AnnouncementHandler opts into PerInstance, so
// every running replica logs each announcement).
app.MapPost("/announcements", async (Announcement announcement, IMessageBus bus) =>
{
    await bus.PublishAsync(announcement);
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
