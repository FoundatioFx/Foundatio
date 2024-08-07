using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions.Hosting.Jobs;
using Foundatio.Extensions.Hosting.Startup;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace Foundatio.HostingSample;

public class Program
{
    public static int Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .CreateLogger();

        try
        {
            Log.Information("Starting host");
            CreateHostBuilder(args).Build().Run();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Host terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();

            if (Debugger.IsAttached)
                Console.ReadKey();
        }
    }

    public static IHostBuilder CreateHostBuilder(string[] args)
    {
        bool all = args.Contains("all", StringComparer.OrdinalIgnoreCase);
        bool sample1 = all || args.Contains("sample1", StringComparer.OrdinalIgnoreCase);
        bool sample2 = all || args.Contains("sample2", StringComparer.OrdinalIgnoreCase);
        bool everyMinute = all || args.Contains("everyMinute", StringComparer.OrdinalIgnoreCase);
        bool evenMinutes = all || args.Contains("evenMinutes", StringComparer.OrdinalIgnoreCase);

        var builder = Host.CreateDefaultBuilder(args)
            .UseSerilog()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.Configure(app =>
                {
                    app.UseSerilogRequestLogging();

                    app.UseHealthChecks("/health");
                    app.UseReadyHealthChecks("Critical");

                    // this middleware will return Service Unavailable until the startup actions have completed
                    app.UseWaitForStartupActionsBeforeServingRequests();

                    // add mvc or other request middleware after the UseWaitForStartupActionsBeforeServingRequests call
                });
            })
            .ConfigureServices(s =>
            {
                // will shutdown the host if no jobs are running
                s.AddJobLifetimeService();
                s.AddSingleton<ICacheClient>(_ => new InMemoryCacheClient());

                // inserts a startup action that does not complete until the critical health checks are healthy
                // gets inserted as 1st startup action so that any other startup actions dont run until the critical resources are available
                s.AddStartupActionToWaitForHealthChecks("Critical");

                s.AddHealthChecks().AddCheck<MyCriticalHealthCheck>("My Critical Resource", tags: new[] { "Critical" });

                // add health check that does not return healthy until the startup actions have completed
                // useful for readiness checks
                s.AddHealthChecks().AddCheckForStartupActions("Critical");

                if (everyMinute)
                    s.AddDistributedCronJob<EveryMinuteJob>("* * * * *");

                if (evenMinutes)
                    s.AddCronJob("EvenMinutes", "*/2 * * * *", async sp =>
                    {
                        var logger = sp.GetRequiredService<ILogger<Program>>();
                        if (logger.IsEnabled(LogLevel.Information))
                            logger.LogInformation("EvenMinuteJob Run Thread={ManagedThreadId}", Thread.CurrentThread.ManagedThreadId);

                        await Task.Delay(TimeSpan.FromSeconds(5));
                    });

                if (sample1)
                    s.AddJob("Sample1", sp => new Sample1Job(sp.GetRequiredService<ILoggerFactory>()), o => o.ApplyDefaults<Sample1Job>().WaitForStartupActions(true).InitialDelay(TimeSpan.FromSeconds(4)));

                if (sample2)
                {
                    s.AddHealthChecks().AddCheck<Sample2Job>("Sample2Job");
                    s.AddJob<Sample2Job>(o => o.WaitForStartupActions(true));
                }

                // if you don't specify priority, actions will automatically be assigned an incrementing priority starting at 0
                s.AddStartupAction("Test1", async sp =>
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
                s.AddStartupAction<MyStartupAction>(priority: 100);
                s.AddStartupAction<OtherStartupAction>(priority: 100);

                s.AddStartupAction("Test2", async sp =>
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
            });

        return builder;
    }
}
