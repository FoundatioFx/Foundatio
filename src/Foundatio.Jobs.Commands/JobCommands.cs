using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Foundatio.Jobs.Commands.Extensions;
using Foundatio.Utility;
using Microsoft.Extensions.CommandLineUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;

namespace Foundatio.Jobs.Commands {
    public class JobCommands {
        public static int Run(string[] args, IServiceProvider serviceProvider, Action<JobCommandsApplication> configure = null, ILoggerFactory loggerFactory = null) {
            return Run(args, () => serviceProvider, configure, loggerFactory);
        }

        public static int Run(string[] args, Func<IServiceProvider> getServiceProvider, Action<JobCommandsApplication> configure = null, ILoggerFactory loggerFactory = null) {
            loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
            var logger = loggerFactory.CreateLogger("JobCommands");
            var lazyServiceProvider = new Lazy<IServiceProvider>(getServiceProvider);

            var app = new JobCommandsApplication {
                Name = "job",
                FullName = "Foundatio Job Runner",
                ShortVersionGetter = () => {
                    try {
                        var versionInfo = FileVersionInfo.GetVersionInfo(Assembly.GetCallingAssembly().Location);
                        return versionInfo.FileVersion;
                    } catch {
                        return String.Empty;
                    }
                },
                LongVersionGetter = () => {
                    try {
                        var versionInfo = FileVersionInfo.GetVersionInfo(Assembly.GetCallingAssembly().Location);
                        return versionInfo.ProductVersion;
                    } catch {
                        return String.Empty;
                    }
                }
            };
            
            var jobConfiguration = GetJobConfiguration(logger);
            app.JobConfiguration = jobConfiguration;

            configure?.Invoke(app);

            var jobTypes = new List<Type>();
            if (app.JobConfiguration.Types != null && app.JobConfiguration.Types.Count > 0) {
                jobTypes.AddRange(app.JobConfiguration.Types);
            } else {
                List<Assembly> assemblies = null;
                if (jobConfiguration.Assemblies != null && jobConfiguration.Assemblies.Count > 0) {
                    assemblies = new List<Assembly>();
                    foreach (string assemblyName in jobConfiguration.Assemblies) {
                        try {
                            var assembly = Assembly.Load(assemblyName);
                            if (assembly != null)
                                assemblies.Add(assembly);
                        }
                        catch (Exception ex) {
                            if (logger.IsEnabled(LogLevel.Error))
                                logger.LogError(ex, "Unable to load job assembly {AssemblyName}", assemblyName);
                        }
                    }
                }

                if (assemblies != null && assemblies.Count == 0)
                    assemblies = null;

                jobTypes.AddRange(TypeHelper.GetDerivedTypes<IJob>(assemblies));
                if (jobConfiguration?.Exclusions != null && jobConfiguration.Exclusions.Count > 0)
                    jobTypes = jobTypes.Where(t => !t.FullName.AnyWildcardMatches(jobConfiguration.Exclusions, true)).ToList();
            }

            foreach (var jobType in jobTypes) {
                var jobOptions = JobOptions.GetDefaults(jobType, () => lazyServiceProvider.Value.GetService(jobType) as IJob);

                app.Command(jobOptions.Name, c => {
                    if (!String.IsNullOrEmpty(jobOptions.Description))
                        c.Description = jobOptions.Description;

                    var configureMethod = jobType.GetMethod("Configure", BindingFlags.Static | BindingFlags.Public);
                    if (configureMethod != null) {
                        configureMethod.Invoke(null, new[] { new JobCommandContext(c, jobType, lazyServiceProvider, loggerFactory) });
                    } else {
                        var isContinuousOption = c.Option("-c --continuous <BOOL>", "Whether the job should be run continuously.", CommandOptionType.SingleValue);
                        var intervalOption = c.Option("-i --interval <INTERVAL>", "The amount of time to delay between job runs when running continuously.", CommandOptionType.SingleValue);
                        var delayOption = c.Option("-d --delay <TIME>", "The amount of time to delay before the initial job run.", CommandOptionType.SingleValue);
                        var limitOption = c.Option("-l --iteration-limit <COUNT>", "The number of times the job should be run before exiting.", CommandOptionType.SingleValue);

                        c.OnExecute(() => {
                            if (isContinuousOption.HasValue())
                                if (Boolean.TryParse(isContinuousOption.Value(), out bool isContinuous))
                                    jobOptions.RunContinuous = isContinuous;

                            if (intervalOption.HasValue())
                                if (TimeUnit.TryParse(intervalOption.Value(), out var interval))
                                    jobOptions.Interval = interval;

                            if (delayOption.HasValue())
                                if (TimeUnit.TryParse(delayOption.Value(), out var delay))
                                    jobOptions.InitialDelay = delay;

                            if (limitOption.HasValue())
                                if (Int32.TryParse(limitOption.Value(), out int limit))
                                    jobOptions.IterationLimit = limit;

                            return new JobRunner(jobOptions, loggerFactory).RunInConsoleAsync();
                        });
                    }
                    c.HelpOption("-?|-h|--help");
                });
            }

            app.Command("run-all", c => {
                c.Description = "Run all jobs with their default configuration.";

                c.OnExecute(() => {
                    var jobTasks = new List<Task>();
                    var cancellationToken = JobRunner.GetShutdownCancellationToken(logger);

                    foreach (var jobType in jobTypes) {
                        var jobOptions = JobOptions.GetDefaults(jobType, () => lazyServiceProvider.Value.GetService(jobType) as IJob);
                        jobTasks.Add(new JobRunner(jobOptions, loggerFactory).RunAsync(cancellationToken));
                    }

                    Task.WaitAll(jobTasks.ToArray());
                    return 0;
                });

                c.HelpOption("-?|-h|--help");
            });

            app.Command("run", c => {
                c.Description = "Runs a job using a fully qualified type name.";
                c.HelpOption("-?|-h|--help");

                var jobArgument = c.Argument("job", "The job name or fully qualified type to run.");
                var isContinuousOption = c.Option("-c --continuous <BOOL>", "Whether the job should be run continuously.", CommandOptionType.SingleValue);
                var intervalOption = c.Option("-i --interval <NAME>", "The amount of time to delay between job runs when running continuously.", CommandOptionType.SingleValue);

                c.OnExecute(() => {
                    Type jobType = null;
                    bool isContinuous = true;
                    TimeSpan? interval = null;

                    if (isContinuousOption.HasValue())
                        Boolean.TryParse(isContinuousOption.Value(), out isContinuous);

                    if (intervalOption.HasValue())
                        TimeUnit.TryParse(intervalOption.Value(), out interval);

                    try {
                        jobType = Type.GetType(jobArgument.Value);
                    } catch (Exception ex) {
                        if (logger.IsEnabled(LogLevel.Error))
                            logger.LogError(ex, "Error getting job type: {Message}", ex.Message);
                    }

                    if (jobType == null)
                        return Task.FromResult(-1);

                    return new JobRunner(() => lazyServiceProvider.Value.GetService(jobType) as IJob, loggerFactory, runContinuous: isContinuous, interval: interval).RunInConsoleAsync();
                });
            });

            app.HelpOption("-?|-h|--help");
            app.VersionOption("-v|--version", app.ShortVersionGetter, app.LongVersionGetter);

            int result = -1;
            try {
                if (args == null || args.Length == 0)
                    app.ShowHelp();
                else
                    result = app.Execute(args);
            } catch (CommandParsingException ex) {
                Console.WriteLine(ex.Message);
                app.ShowHelp();
            }

            if (Debugger.IsAttached) {
                Console.WriteLine($"<result {result}>");
                Console.Write("Press any key to continue ");
                Console.ReadKey();
                Console.WriteLine();
            }

            return result;
        }

        private static JobConfiguration GetJobConfiguration(ILogger logger) {
            if (!File.Exists("jobs.json"))
                return new JobConfiguration();

            string jobConfig = File.ReadAllText("jobs.json");
            try {
                return JsonConvert.DeserializeObject<JobConfiguration>(jobConfig);
            } catch (Exception ex) {
                if (logger.IsEnabled(LogLevel.Error))
                    logger.LogError(ex, "Error parsing job config file: {Message}", ex.Message);
                return new JobConfiguration();
            }
        }
    }

    public class JobCommandsApplication : CommandLineApplication {
        public JobConfiguration JobConfiguration { get; set; }
    }

    public class JobConfiguration {
        /// <summary>
        /// A list of job types to use. If this collection is populated, it will override Assemblies and Exclusions.
        /// </summary>
        public ICollection<Type> Types { get; set; }

        /// <summary>
        /// List of assemblies to inspect for types that implement IJob.
        /// </summary>
        public ICollection<string> Assemblies { get; set; }

        /// <summary>
        /// List of exclusion patterns that will be applied to the jobs discovered in the specified assemblies.
        /// </summary>
        public ICollection<string> Exclusions { get; set; }
    }
}
