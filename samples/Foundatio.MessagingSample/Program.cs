using Foundatio;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Foundatio.MessagingSample;

var builder = WebApplication.CreateBuilder(args);

// A short id so log lines make it obvious WHICH instance handled each message/job when scaled to multiple replicas.
builder.Services.AddSingleton(new InstanceInfo(Guid.NewGuid().ToString("N")[..6]));

builder.Services.AddFoundatioWorker(foundatio => foundatio
    // Queue consumers compete; each named event subscription receives its own copy.
    .Messaging.UseAws()
    .Messaging.AddConsumer<ProcessOrder, ProcessOrderHandler>()
    .Messaging.AddSubscriber<Announcement, AnnouncementHandler>("announcements") // one replica in this durable subscriber group
                                                                                 // Persisted jobs on Redis.
    .Jobs.UseRedis()
    .Jobs.AddJobType<GenerateReportJob>("generate-report")                      // on-demand, submitted via POST /reports
    .Jobs.AddCronJob<HeartbeatJob>("* * * * *")                               // Global: one instance per tick
    .Jobs.AddCronJob<RefreshCacheJob>("* * * * *", o => o.Scope = ScheduledJobScope.PerNode) // every instance per tick
    .Jobs.AddCronJob<SweepStaleOrdersJob>("*/2 * * * *"));                     // Global: periodic sweep

var app = builder.Build();

app.MapGet("/", (InstanceInfo instance) => Results.Ok(new { service = "Foundatio messaging sample", instance = instance.Id }));

// SEND — a command / unit of work: replicas compete to process each order (handled by ProcessOrderHandler).
app.MapPost("/orders", async (ProcessOrder order, IMessageBus bus) =>
    Results.Accepted(value: new { queued = await bus.SendAsync(order) }));

// PUBLISH — one copy for the durable announcements group; its replicas compete.
app.MapPost("/announcements", async (Announcement announcement, IMessageBus bus) =>
{
    await bus.PublishAsync(announcement);
    return Results.Accepted(value: new { published = announcement.Text });
});

// DURABLE JOB — submitted here with typed arguments (persisted in the job payload; the job reads them back with
// context.GetArguments<ReportArgs>()), executed on whichever instance's job worker claims it.
app.MapPost("/reports", async (IJobClient jobs) =>
{
    var handle = await jobs.EnqueueAsync<GenerateReportJob, ReportArgs>(new ReportArgs("pdf", "sample-user"));
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
