using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions.Hosting.Jobs;
using Foundatio.Extensions.Hosting.Startup;
using Foundatio.HostingSample;
using Foundatio.Lock;
using Foundatio.Messaging;
using Foundatio.Serializer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
#if REDIS
using StackExchange.Redis;
#endif

bool all = args.Contains("all", StringComparer.OrdinalIgnoreCase);
bool sample1 = all || args.Contains("sample1", StringComparer.OrdinalIgnoreCase);
bool sample2 = all || args.Contains("sample2", StringComparer.OrdinalIgnoreCase);
bool everyMinute = all || args.Contains("everyMinute", StringComparer.OrdinalIgnoreCase);
bool evenMinutes = all || args.Contains("evenMinutes", StringComparer.OrdinalIgnoreCase);

var builder = WebApplication.CreateBuilder(args);

ConfigureServices(builder);

// inserts a startup action that does not complete until the critical health checks are healthy
// gets inserted as 1st startup action so that any other startup actions don't run until the critical resources are available
builder.Services.AddStartupActionToWaitForHealthChecks("Critical");

builder.Services.AddHealthChecks().AddCheck<MyCriticalHealthCheck>("My Critical Resource", tags: ["Critical"]);

// add health check that does not return healthy until the startup actions have completed
// useful for readiness checks
builder.Services.AddHealthChecks().AddCheckForStartupActions("Critical");

// this gets added automatically by any AddJob call, but we might not be running any jobs, and we need it for doing dynamic jobs
builder.Services.AddJobScheduler();

if (everyMinute)
    builder.Services.AddDistributedCronJob<EveryMinuteJob>("* * * * *");

builder.Services.AddCronJob(b => b.Name("Tokyo").CronSchedule("44 4 * * *").CronTimeZone("Asia/Tokyo").JobAction(async sp =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Tokyo 4:44am Run Thread={ManagedThreadId}", Thread.CurrentThread.ManagedThreadId);
    await Task.Delay(TimeSpan.FromSeconds(5));
}));

if (evenMinutes)
    builder.Services.AddCronJob("EvenMinutes", "#1#2 * * * *", async sp =>
    {
        var logger = sp.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("EvenMinuteJob Run Thread={ManagedThreadId}", Thread.CurrentThread.ManagedThreadId);
        await Task.Delay(TimeSpan.FromSeconds(30));
        logger.LogInformation("EvenMinuteJob Complete");
    });

if (sample1)
    builder.Services.AddJob("Sample1", sp => new Sample1Job(sp.GetRequiredService<ILoggerFactory>()), o => o.ApplyDefaults<Sample1Job>().WaitForStartupActions().InitialDelay(TimeSpan.FromSeconds(4)));

builder.Services.AddJob<SampleLockJob>(o => o.WaitForStartupActions());

if (sample2)
{
    builder.Services.AddHealthChecks().AddCheck<Sample2Job>("Sample2Job");
    builder.Services.AddJob<Sample2Job>(o => o.WaitForStartupActions());
}

// if you don't specify priority, actions will automatically be assigned an incrementing priority starting at 0
builder.Services.AddStartupAction("Test1", async sp =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    logger.LogTrace("Running startup 1 action");
    for (int i = 0; i < 3; i++)
    {
        await Task.Delay(1000);
        logger.LogTrace("Running startup 1 action...");
    }

    logger.LogTrace("Done running startup 1 action");
});

// then these startup actions will run concurrently since they both have the same priority
builder.Services.AddStartupAction<MyStartupAction>(priority: 100);
builder.Services.AddStartupAction<OtherStartupAction>(priority: 100);

builder.Services.AddStartupAction("Test2", async sp =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    logger.LogTrace("Running startup 2 action");
    for (int i = 0; i < 2; i++)
    {
        await Task.Delay(1500);
        logger.LogTrace("Running startup 2 action...");
    }
    //throw new ApplicationException("Boom goes the startup");
    logger.LogTrace("Done running startup 2 action");
});

//s.AddStartupAction("Boom", () => throw new ApplicationException("Boom goes the startup"));

var app = builder.Build();

app.MapGet("/", () => "Foundatio!");

app.MapGet("/jobs/status", (IJobManager jobManager, HttpRequest req) =>
    {
        var jobName = req.Query["name"];
        if (!String.IsNullOrEmpty(jobName))
            return Results.Ok(jobManager.GetJobStatus(jobName));

        return Results.Ok(jobManager.GetJobStatus());
    });

app.MapGet("/jobs/run", async (IJobManager jobManager, HttpRequest req) =>
    {
        var jobName = req.Query["name"];
        if (string.IsNullOrWhiteSpace(jobName))
            return Results.BadRequest("Job name is required.");

        await jobManager.RunJobAsync(jobName);

        return Results.Accepted();
    });

app.MapGet("/jobs/enable", (IJobManager jobManager, HttpRequest req) =>
    {
        var jobName = req.Query["name"];
        if (string.IsNullOrWhiteSpace(jobName))
            return Results.BadRequest("Job name is required.");

        jobManager.Update(jobName, c => c.Enabled());

        return Results.Accepted();
    });

app.MapGet("/jobs/disable", (IJobManager jobManager, HttpRequest req) =>
    {
        var jobName = req.Query["name"];
        if (string.IsNullOrWhiteSpace(jobName))
            return Results.BadRequest("Job name is required.");

        jobManager.Update(jobName, c => c.Disabled());

        return Results.Accepted();
    });

app.UseHealthChecks("/health");
app.UseReadyHealthChecks("Critical");

// this middleware will return Service Unavailable until the startup actions have completed
app.UseWaitForStartupActionsBeforeServingRequests();

// add mvc or other request middleware after the UseWaitForStartupActionsBeforeServingRequests call

app.Run();

void ConfigureServices(WebApplicationBuilder builder)
{
    builder.Services.AddLogging(opt =>
    {
        opt.AddSimpleConsole(c => c.TimestampFormat = "[HH:mm:ss] ");
    });

    builder.AddServiceDefaults();
    builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.WriteIndented = true; });

    // shutdown the host if no jobs are running
    builder.Services.AddJobLifetimeService();

#if REDIS
    builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    {
        var connectionString = builder.Configuration.GetConnectionString("Redis")!;
        connectionString += ",abortConnect=false";
        return ConnectionMultiplexer.Connect(connectionString, o => o.LoggerFactory = sp.GetRequiredService<ILoggerFactory>());
    });

    // distributed cache
    builder.Services.AddSingleton<ISerializer, SystemTextJsonSerializer>();
    builder.Services.AddSingleton<ITextSerializer, SystemTextJsonSerializer>();
    builder.Services.AddSingleton<ICacheClient>(sp => new RedisCacheClient(c => c.ConnectionMultiplexer(sp.GetRequiredService<IConnectionMultiplexer>())));

    // distributed lock provider
    builder.Services.AddSingleton(s => new CacheLockProvider(s.GetRequiredService<ICacheClient>(), s.GetRequiredService<IMessageBus>(), s.GetRequiredService<ILoggerFactory>()));
    builder.Services.AddSingleton<ILockProvider>(s => s.GetRequiredService<CacheLockProvider>());

    // distributed message bus
    builder.Services.AddSingleton<IMessageBus>(s => new RedisMessageBus(new RedisMessageBusOptions
    {
        Subscriber = s.GetRequiredService<IConnectionMultiplexer>().GetSubscriber(),
        Serializer = s.GetRequiredService<ISerializer>(),
        LoggerFactory = s.GetRequiredService<ILoggerFactory>()
    }));
    builder.Services.AddSingleton<IMessagePublisher>(s => s.GetRequiredService<IMessageBus>());
    builder.Services.AddSingleton<IMessageSubscriber>(s => s.GetRequiredService<IMessageBus>());
#else
    builder.Services.AddSingleton<ICacheClient>(sp => new InMemoryCacheClient(o => o.LoggerFactory(sp.GetService<ILoggerFactory>())));
#endif
}
