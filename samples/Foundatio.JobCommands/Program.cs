using System;
using CommandLine;
using Foundatio.Jobs.Hosting;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Foundatio.JobCommands {
    public class Program {
        public static int Main(string[] args) {
            try {
                CreateWebHostBuilder(args).Build().Run();
                return 0;
            } catch (Exception ex) {
                Log.Fatal(ex, "Job host terminated unexpectedly");
                return 1;
            } finally {
                Log.CloseAndFlush();
            }
        }
                
        public static IWebHostBuilder CreateWebHostBuilder(string[] args) {
            var options = new Options();
            Parser.Default.ParseArguments<Options>(args).WithParsed(o => options = o);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Console()
                .CreateLogger();

            var builder = WebHost.CreateDefaultBuilder(args)
                .UseSerilog(Log.Logger)
                .SuppressStatusMessages(true)
                .ConfigureServices(s => {
                    s.AddJobLifetime();
                    var healthCheckBuilder = s.AddHealthChecks();

                    if (options.Sample1)
                        s.AddJob<Sample1Job>();

                    if (options.Sample2) {
                        healthCheckBuilder.AddJobCheck<Sample2Job>();
                        s.AddJob<Sample2Job>();
                    }
                })
                .Configure(app => {
                    app.UseHealthChecks("/health");
                });

            return builder;
        }
    }

    public class Options {
        [Option('1', "sample1", Required = false, HelpText = "Set to run the sample 1 job.")]
        public bool Sample1 { get; set; }

        [Option('2', "sample2", Required = false, HelpText = "Set to run the sample 2 job.")]
        public bool Sample2 { get; set; }
    }
}
