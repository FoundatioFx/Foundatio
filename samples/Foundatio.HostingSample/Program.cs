using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio;
using Foundatio.Extensions.Hosting.Jobs;
using Foundatio.Extensions.Hosting.Startup;
using Foundatio.HostingSample;
using Foundatio.Resilience;
using Foundatio.Serializer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
#if REDIS
using Microsoft.Extensions.Configuration;
using Foundatio.Redis;
using StackExchange.Redis;
#endif

bool all = args.Contains("all", StringComparer.OrdinalIgnoreCase);
bool sample1 = all || args.Contains("sample1", StringComparer.OrdinalIgnoreCase);
bool sample2 = all || args.Contains("sample2", StringComparer.OrdinalIgnoreCase);
bool everyMinute = all || args.Contains("everyMinute", StringComparer.OrdinalIgnoreCase);
bool evenMinutes = all || args.Contains("evenMinutes", StringComparer.OrdinalIgnoreCase);

var builder = WebApplication.CreateBuilder(args);

// configure Foundatio services
builder.Services.AddFoundatio()
    .Storage.UseFolder()
    .Caching.UseInMemory()
    .Locking.UseCache()
    .Messaging.UseInMemory()
    .AddSerializer(sp => new SystemTextJsonSerializer(sp.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions))
    .AddResilience(b => b.WithPolicy<Sample1Job>(p => p.WithMaxAttempts(5).WithLinearDelay().WithJitter()));

ConfigureServices();

// shutdown the host if no jobs are running, cron jobs are not considered running jobs
builder.Services.AddJobLifetimeService();

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
    builder.Services.AddCronJob("EvenMinutes", "*/2 * * * *", async sp =>
    {
        var logger = sp.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("EvenMinuteJob Run Thread={ManagedThreadId}", Thread.CurrentThread.ManagedThreadId);
        await Task.Delay(TimeSpan.FromSeconds(30));
        logger.LogInformation("EvenMinuteJob Complete");
    });

if (sample1)
    builder.Services.AddJob("Sample1", sp => new Sample1Job(sp.GetService<IResiliencePolicyProvider>(), sp.GetService<ILoggerFactory>()), o => o.ApplyDefaults<Sample1Job>().WaitForStartupActions().InitialDelay(TimeSpan.FromSeconds(4)));

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
        await Task.Delay(100);
        logger.LogTrace("Running startup 1 action...");
    }

    logger.LogTrace("Done running startup 1 action");
});

// then these startup actions will run concurrently since they both have the same priority
builder.Services.AddStartupAction<MyStartupAction>(priority: 100);
builder.Services.AddStartupAction<OtherStartupAction>(priority: 100);

/*builder.Services.AddStartupAction("Test2", async sp =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    logger.LogTrace("Running startup 2 action");
    for (int i = 0; i < 2; i++)
    {
        await Task.Delay(50);
        logger.LogTrace("Running startup 2 action...");
    }
    //throw new ApplicationException("Boom goes the startup");
    logger.LogTrace("Done running startup 2 action");
});*/

//s.AddStartupAction("Boom", () => throw new ApplicationException("Boom goes the startup"));

var app = builder.Build();

app.MapGet("/", () => "Foundatio!");

app.MapGet("/jobs/status", (IJobManager jobManager, string name = null, bool? running = null, bool history = true) =>
    {
        if (!String.IsNullOrEmpty(name))
            return Results.Ok(jobManager.GetJobStatus(name, includeHistory: history));

        if (running.HasValue && running.Value)
            return Results.Ok(jobManager.GetJobStatus(true, includeHistory: history));

        return Results.Ok(jobManager.GetJobStatus(includeHistory: history));
    });

app.MapGet("/jobs/run", async (IJobManager jobManager, string name) =>
    {
        if (String.IsNullOrWhiteSpace(name))
            return Results.BadRequest("Job name is required.");

        await jobManager.RunJobAsync(name);

        return Results.Accepted($"Job {name} started successfully.");
    });

app.MapGet("/jobs/enable", (IJobManager jobManager, string name) =>
    {
        if (String.IsNullOrWhiteSpace(name))
            return Results.BadRequest("Job name is required.");

        jobManager.Update(name, c => c.Enabled());

        return Results.Ok($"Job {name} enabled successfully.");
    });

app.MapGet("/jobs/disable", (IJobManager jobManager, string name) =>
    {
        if (String.IsNullOrWhiteSpace(name))
            return Results.BadRequest("Job name is required.");

        jobManager.Update(name, c => c.Disabled());

        return Results.Ok($"Job {name} disabled successfully.");
    });

app.MapGet("/jobs/schedule", (IJobManager jobManager, string name, string cron) =>
{
    if (String.IsNullOrWhiteSpace(name))
        return Results.BadRequest("Job name is required.");

    jobManager.Update(name, c => c.CronSchedule(cron));

    return Results.Ok($"Job {name} updated successfully.");
});

app.MapGet("/jobs/release", async (IJobManager jobManager, string name) =>
{
    if (String.IsNullOrWhiteSpace(name))
        return Results.BadRequest("Job name is required.");

    await jobManager.ReleaseLockAsync(name);

    return Results.Ok($"Job {name} lock released successfully.");
});

app.UseHealthChecks("/health");
app.UseReadyHealthChecks("Critical");

// this middleware will return Service Unavailable until the startup actions have completed
app.UseWaitForStartupActionsBeforeServingRequests();

// add mvc or other request middleware after the UseWaitForStartupActionsBeforeServingRequests call

app.Run();

void ConfigureServices()
{
    builder.Services.AddLogging(opt =>
    {
        opt.AddSimpleConsole(c => c.TimestampFormat = "[HH:mm:ss] ");
    });

    builder.AddServiceDefaults();
    builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.WriteIndented = true; });

#if REDIS
    builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    {
        var connectionString = builder.Configuration.GetConnectionString("Redis")!;
        connectionString += ",abortConnect=false";
        return ConnectionMultiplexer.Connect(connectionString);
        // enable redis logging
        //return ConnectionMultiplexer.Connect(connectionString, o => o.LoggerFactory = sp.GetRequiredService<ILoggerFactory>());
    });

    // distributed cache and messaging using redis (replaces in memory cache)
    builder.Services.AddFoundatio()
        .Caching.UseRedis()
        .Messaging.UseRedis();
#endif
}
