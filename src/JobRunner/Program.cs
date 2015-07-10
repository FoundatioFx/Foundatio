using System;
using System.Diagnostics;
using System.IO;
using CommandLine;
using Foundatio.Extensions;
using Foundatio.Jobs;
using Foundatio.ServiceProviders;
using Foundatio.Logging;

namespace Foundatio.JobRunner {
    internal class Program {
        private static int Main(string[] args) {
            int result;
            string jobName = "N/A";
            try {
                var ca = new Options();
                if (!Parser.Default.ParseArguments(args, ca)) {
                    PauseIfDebug();
                    return 0;
                }

                if (!ca.Quiet)
                    OutputHeader();

                var jobType = !String.IsNullOrEmpty(ca.JobType) ? Type.GetType(ca.JobType) : null;
                if (jobType != null)
                    jobName = jobType.Name;

                Logger.GlobalProperties.Set("job", jobName);
                if (!(ca.NoServiceProvider.HasValue && ca.NoServiceProvider.Value == false))
                    ServiceProvider.SetServiceProvider(ca.ServiceProviderType, ca.JobType);

                // force bootstrap now so logging will be configured
                if (ServiceProvider.Current is IBootstrappedServiceProvider)
                    ((IBootstrappedServiceProvider)ServiceProvider.Current).Bootstrap();

                result = Jobs.JobRunner.RunAsync(new JobRunOptions {
                    JobTypeName = ca.JobType,
                    InstanceCount = ca.InstanceCount,
                    Interval = TimeSpan.FromMilliseconds(ca.Delay),
                    RunContinuous = ca.RunContinuously
                }).Result;

                PauseIfDebug();
            } catch (FileNotFoundException e) {
                Console.Error.WriteLine("{0} ({1})", e.GetMessage(), e.FileName);
                Logger.Error().Message(String.Format("{0} ({1})", e.GetMessage(), e.FileName)).Write();

                PauseIfDebug();
                return 1;
            } catch (Exception e) {
                Console.Error.WriteLine(e.ToString());
                Logger.Error().Exception(e).Message(String.Format("Job \"{0}\" error: {1}", jobName, e.GetMessage())).Write();

                PauseIfDebug();
                return 1;
            }

            return result;
        }

        private static void OutputHeader() {
            Console.WriteLine("Foundatio Job Runner v{0}", GetInformationalVersion());
            Console.WriteLine();
        }

        internal static string GetInformationalVersion() {
            return FileVersionInfo.GetVersionInfo(typeof(Program).Assembly.Location).ProductVersion;
        }

        private static void PauseIfDebug() {
            if (Debugger.IsAttached)
                Console.ReadKey();
        }
    }
}