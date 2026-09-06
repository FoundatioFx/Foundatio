using Foundatio;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Foundatio.MessagingSample;

var builder = WebApplication.CreateBuilder(args);

// A short id so log lines make it obvious WHICH instance handled each message/job when scaled to multiple replicas.
builder.Services.AddSingleton(new InstanceInfo(Guid.NewGuid().ToString("N")[..6]));

builder.Services.AddFoundatioWorker(foundatio => foundatio
    .UseServiceName("messaging-sample")
    .ConfigureMessaging(messaging => messaging.UseAws()
        .AddConsumer<ProcessOrder, ProcessOrderHandler>()
        .AddSubscriber<Announcement, AnnouncementHandler>())
    .ConfigureJobs(jobs => jobs.UseRedis()
        .AddJobType<GenerateReportJob>("generate-report")
        .AddCronJob<HeartbeatJob>("* * * * *")
        .AddCronJob<RefreshCacheJob>("* * * * *")
        .AddCronJob<SweepStaleOrdersJob>("*/2 * * * *")));

var app = builder.Build();
app.MapHealthChecks("/health");

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
