using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions.Hosting.Jobs;
using Foundatio.Extensions.Hosting.Startup;
using Foundatio.HostingSample;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

try
{
    Log.Information("Starting web application");

    bool all = args.Contains("all", StringComparer.OrdinalIgnoreCase);
    bool sample1 = all || args.Contains("sample1", StringComparer.OrdinalIgnoreCase);
    bool sample2 = all || args.Contains("sample2", StringComparer.OrdinalIgnoreCase);
    bool everyMinute = all || args.Contains("everyMinute", StringComparer.OrdinalIgnoreCase);
    bool evenMinutes = all || args.Contains("evenMinutes", StringComparer.OrdinalIgnoreCase);

    var builder = WebApplication.CreateBuilder(args);

    builder.AddServiceDefaults();

    builder.Services.AddSerilog();

    // shutdown the host if no jobs are running
    builder.Services.AddJobLifetimeService();
    builder.Services.AddSingleton<ICacheClient>(sp => new InMemoryCacheClient(o => o.LoggerFactory(sp.GetService<ILoggerFactory>())));

    // inserts a startup action that does not complete until the critical health checks are healthy
    // gets inserted as 1st startup action so that any other startup actions don't run until the critical resources are available
    builder.Services.AddStartupActionToWaitForHealthChecks("Critical");

    builder.Services.AddHealthChecks().AddCheck<MyCriticalHealthCheck>("My Critical Resource", tags: ["Critical"]);

    // add health check that does not return healthy until the startup actions have completed
    // useful for readiness checks
    builder.Services.AddHealthChecks().AddCheckForStartupActions("Critical");

    // this gets added automatically by any AddJob call, but we might not be running any jobs and we need it for doing dynamic jobs
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
            await Task.Delay(TimeSpan.FromSeconds(5));
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

    app.UseSerilogRequestLogging();

    app.MapGet("/", () => "Foundatio!");

    app.MapGet("/jobstatus", httpContext =>
    {
        var jobManager = httpContext.RequestServices.GetRequiredService<JobManager>();
        var status = jobManager.GetJobStatus();
        return httpContext.Response.WriteAsJsonAsync(status);
    });

    app.MapGet("/runjob", async httpContext =>
    {
        var jobManager = httpContext.RequestServices.GetRequiredService<JobManager>();
        await jobManager.RunJobAsync("EvenMinutes");
        await jobManager.RunJobAsync<EveryMinuteJob>();
    });

    app.UseHealthChecks("/health");
    app.UseReadyHealthChecks("Critical");

    // this middleware will return Service Unavailable until the startup actions have completed
    app.UseWaitForStartupActionsBeforeServingRequests();

    // add mvc or other request middleware after the UseWaitForStartupActionsBeforeServingRequests call

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    return 1;
}
finally
{
    Log.CloseAndFlush();

    if (Debugger.IsAttached)
        Console.ReadKey();
}

return 0;

