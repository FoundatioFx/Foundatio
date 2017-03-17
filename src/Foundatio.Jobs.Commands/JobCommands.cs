using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Exceptionless.DateTimeExtensions;
using Foundatio.Jobs.Commands.Extensions;
using Foundatio.Logging;
using Microsoft.Extensions.CommandLineUtils;
using Newtonsoft.Json;

namespace Foundatio.Jobs.Commands {
    public class JobCommands {
        public static int Run(string[] args, Func<IServiceProvider> getServiceProvider, Action<CommandLineApplication> configure = null, ILoggerFactory loggerFactory = null) {
            var logger = loggerFactory.CreateLogger("JobCommands");
            var lazyServiceProvider = new Lazy<IServiceProvider>(getServiceProvider);

            var app = new CommandLineApplication {
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

            configure?.Invoke(app);

            var jobConfiguration = GetJobConfiguration(logger);
            List<Assembly> assemblies = null;
            if (jobConfiguration.Assemblies != null && jobConfiguration.Assemblies.Count > 0) {
                assemblies = new List<Assembly>();
                foreach (var assemblyName in jobConfiguration.Assemblies) {
                    try {
                        var assembly = Assembly.Load(assemblyName);
                        if (assembly != null)
                            assemblies.Add(assembly);
                    } catch (Exception ex) {
                        logger.Error(ex, $"Unable to load job assembly \"{assemblyName}\"");
                    }
                }
            }

            if (assemblies != null && assemblies.Count == 0)
                assemblies = null;

            var jobTypes = TypeHelper.GetDerivedTypes<IJob>(assemblies);
            if (jobConfiguration?.Exclusions != null && jobConfiguration.Exclusions.Count > 0)
                jobTypes = jobTypes.Where(t => !t.FullName.AnyWildcardMatches(jobConfiguration.Exclusions, true)).ToList();

            foreach (var jobType in jobTypes) {
                var jobAttribute = jobType.GetCustomAttribute<JobAttribute>() ?? new JobAttribute();
                string jobName = jobAttribute.Name;

                if (String.IsNullOrEmpty(jobName)) {
                    jobName = jobType.Name;
                    if (jobName.EndsWith("Job"))
                        jobName = jobName.Substring(0, jobName.Length - 3);

                    jobName = jobName.ToLower();
                }

                app.Command(jobName, c => {
                    if (!String.IsNullOrEmpty(jobAttribute.Description))
                        c.Description = jobAttribute.Description;

                    MethodInfo configureMethod = jobType.GetMethod("Configure", BindingFlags.Static | BindingFlags.Public);
                    if (configureMethod != null) {
                        configureMethod.Invoke(null, new[] { new JobCommandContext(c, jobType, lazyServiceProvider, loggerFactory) });
                    } else {
                        var isContinuousOption = c.Option("-c --continuous <BOOL>", "Wether the job should be run continuously.", CommandOptionType.SingleValue);
                        var intervalOption = c.Option("-i --interval <INTERVAL>", "The amount of time to delay between job runs when running continuously.", CommandOptionType.SingleValue);
                        var delayOption = c.Option("-d --delay <TIME>", "The amount of time to delay before the initial job run.", CommandOptionType.SingleValue);
                        var limitOption = c.Option("-l --iteration-limit <COUNT>", "The number of times the job should be run before exiting.", CommandOptionType.SingleValue);

                        c.OnExecute(() => {
                            bool isContinuous = jobAttribute.IsContinuous;
                            TimeSpan? interval = null;
                            TimeSpan? delay = null;
                            int limit = -1;

                            if (isContinuousOption.HasValue())
                                Boolean.TryParse(isContinuousOption.Value(), out isContinuous);

                            if (!String.IsNullOrEmpty(jobAttribute.Interval))
                                TimeUnit.TryParse(jobAttribute.Interval, out interval);

                            if (intervalOption.HasValue())
                                TimeUnit.TryParse(intervalOption.Value(), out interval);

                            if (!String.IsNullOrEmpty(jobAttribute.InitialDelay))
                                TimeUnit.TryParse(jobAttribute.InitialDelay, out delay);

                            if (delayOption.HasValue())
                                TimeUnit.TryParse(delayOption.Value(), out delay);

                            if (jobAttribute.IterationLimit > 0)
                                limit = jobAttribute.IterationLimit;

                            if (limitOption.HasValue())
                                Int32.TryParse(limitOption.Value(), out limit);

                            var job = lazyServiceProvider.Value.GetService(jobType) as IJob;
                            return new JobRunner(job, loggerFactory, runContinuous: isContinuous, interval: interval, initialDelay: delay, iterationLimit: limit).RunInConsole();
                        });
                    }
                    c.HelpOption("-?|-h|--help");
                });
            }

            app.Command("run", c => {
                c.Description = "Runs a job using a fully qualified type name.";
                c.HelpOption("-?|-h|--help");

                var jobArgument = c.Argument("job", "The job name or fully qualified type to run.");
                var isContinuousOption = c.Option("-c --continuous <BOOL>", "Wether the job should be run continuously.", CommandOptionType.SingleValue);
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
                        logger.Error(ex, $"Error getting job type: {ex.Message}");
                    }

                    if (jobType == null)
                        return -1;

                    return new JobRunner(() => lazyServiceProvider.Value.GetService(jobType) as IJob, loggerFactory, runContinuous: isContinuous, interval: interval).RunInConsole();
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

            var jobConfig = File.ReadAllText("jobs.json");
            try {
                return JsonConvert.DeserializeObject<JobConfiguration>(jobConfig);
            } catch (Exception ex) {
                logger.Error(ex, $"Error parsing job config file: {ex.Message}");
                return new JobConfiguration();
            }
        }
    }

    public class JobConfiguration {
        public ICollection<string> Assemblies { get; set; }
        public ICollection<string> Exclusions { get; set; }
    }
}
