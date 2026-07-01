using Amazon;
using Amazon.Runtime;
using Foundatio;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Foundatio.MessagingSample;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// A short id so log lines make it obvious WHICH instance handled each message/job when scaled to multiple replicas.
var instance = new InstanceInfo(Guid.NewGuid().ToString("N")[..6]);
builder.Services.AddSingleton(instance);

// One shared Redis connection: it backs the durable job runtime, and (when selected) the messaging transport too.
string redisConnectionString = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6399";
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnectionString));

// The messaging transport is chosen at startup — both AWS (SQS/SNS) and Redis (Streams) are wired, so you can flip
// Messaging:Provider and compare them without touching a line of the queue/pub-sub code below.
string transport = builder.Configuration["Messaging:Provider"] ?? "Redis";

builder.Services.AddFoundatio()
    // Queues (competing consumers) and pub/sub (fan-out) both ride this single transport.
    .Messaging.UseTransport(sp => transport.Equals("Aws", StringComparison.OrdinalIgnoreCase)
        ? CreateAwsTransport(builder.Configuration)
        : new RedisStreamsMessageTransport(new RedisStreamsMessageTransportOptions
        {
            ConnectionMultiplexer = sp.GetRequiredService<IConnectionMultiplexer>()
        }))
    // Durable jobs live in Redis so any instance can claim and run them. UseRuntimeStore also auto-registers the pump
    // that materializes CRON occurrences, drains scheduled work, and runs submitted jobs.
    .Jobs.UseRuntimeStore(sp => new RedisJobRuntimeStore(new RedisJobRuntimeStoreOptions
    {
        ConnectionMultiplexer = sp.GetRequiredService<IConnectionMultiplexer>()
    }))
    .Jobs.Register<GenerateReportJob>("generate-report")
    .Jobs.Register<HeartbeatJob>("heartbeat");

// Hosts this instance's queue consumer + pub/sub subscriber for the app lifetime.
builder.Services.AddHostedService<MessagingWorkers>();

var app = builder.Build();

// Recurring job: every instance registers the same schedule, but the shared Redis store dedupes each occurrence, so
// exactly one instance runs each tick.
await app.Services.GetRequiredService<IJobScheduler>().ScheduleAsync(new ScheduledJobDefinition
{
    Name = "heartbeat",
    Cron = "* * * * *", // every minute
    JobType = typeof(HeartbeatJob)
});

app.MapGet("/", (InstanceInfo i) => Results.Ok(new { service = "Foundatio messaging sample", instance = i.Id, transport }));

// QUEUE — competing consumers: exactly one instance processes each order.
app.MapPost("/orders", async (ProcessOrder order, IQueue queue) =>
{
    string id = await queue.EnqueueAsync(order);
    return Results.Accepted(value: new { queued = id });
});

// PUB/SUB — fan-out: every instance receives each announcement.
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

// LocalStack (provisioned by the AppHost) provides AWS SQS/SNS locally and accepts any credentials. AutoCreateDestinations
// creates queues/topics on first use; ResourcePrefix keeps this sample's resources namespaced.
static AwsMessageTransport CreateAwsTransport(IConfiguration configuration)
{
    return new AwsMessageTransport(new AwsMessageTransportOptions
    {
        ServiceUrl = configuration["Aws:ServiceUrl"] ?? "http://localhost:4566",
        Region = RegionEndpoint.USEast1,
        Credentials = new BasicAWSCredentials("test", "test"),
        AutoCreateDestinations = true,
        ResourcePrefix = "fnd-sample-"
    });
}
