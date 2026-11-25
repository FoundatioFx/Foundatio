using Foundatio;
using Foundatio.Caching;
using Foundatio.Extensions.Hosting.Jobs;
using Foundatio.Extensions.Hosting.Startup;
using Foundatio.Jobs;
using Foundatio.Messaging;
using Foundatio.Queues;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Polly;
using Polly.Retry;
using StackExchange.Redis;
using V13App;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect("localhost,abortConnect=false"));
builder.Services.AddFoundatio()
    .Caching.UseInMemory()
    .Messaging.UseInMemory()
    .Locking.UseCache()
    .Storage.UseInMemory()
    .Queueing.UseInMemory<WorkItem>();
builder.Services.AddSingleton<ICacheClient>(sp => new RedisCacheClient(o => o.ConnectionMultiplexer(sp.GetRequiredService<IConnectionMultiplexer>())));
builder.Services.AddSingleton<IMessageBus>(sp => new RedisMessageBus(o => o.Subscriber(sp.GetRequiredService<IConnectionMultiplexer>().GetSubscriber())));
builder.Services.AddJob<CleanupJob>(o => o.WaitForStartupActions());
builder.Services.AddJob<WorkItemQueueJob>();
builder.Services.AddCronJob<ReportJob>("0 * * * *");
builder.Services.AddDistributedCronJob<ReportJob>("30 * * * *");
builder.Services.AddJobScheduler();
builder.Services.AddStartupAction("migrate", () => { });
builder.Services.AddHealthChecks().AddCheck<QueueHealthCheck>("queue", HealthStatus.Degraded);

RetryPolicy retry = Policy.Handle<TimeoutException>().Retry(3);
AsyncRetryPolicy asyncRetry = Policy.Handle<HttpRequestException>().WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(attempt));
ResiliencePipeline pipeline = new ResiliencePipelineBuilder().AddRetry(new RetryStrategyOptions { MaxRetryAttempts = 3 }).Build();
builder.Services.AddSingleton(retry);
builder.Services.AddSingleton(asyncRetry);
builder.Services.AddSingleton(pipeline);
builder.Services.AddSingleton<OrderService>();

var app = builder.Build();
app.MapHealthChecks("/health");
app.MapGet("/jobs", (IJobManager jobManager) =>
{
    JobStatus[] statuses = jobManager.GetJobStatus();
    return Results.Ok(statuses.Select(s => new { s.Name, s.Running }));
});
app.MapPost("/orders/{id:int}", async (int id, OrderService orders, CancellationToken cancellationToken) =>
{
    await orders.PlaceAsync(id, cancellationToken);
    return Results.Accepted();
});

app.Run();
