using System;
using System.Diagnostics;
using CommandLine;
using Foundatio.Jobs;

namespace Foundatio.JobRunner {
    internal class Program {
        private static int Main(string[] args) {
            var ca = new Options();
            if (!Parser.Default.ParseArguments(args, ca)) {
                if (Debugger.IsAttached)
                    Console.ReadKey();
                return 0;
            }

            if (!ca.Quiet)
                OutputHeader();

            return Jobs.JobRunner.RunInConsole(new JobRunOptions {
                JobTypeName = ca.JobType,
                ServiceProviderTypeName = ca.ServiceProviderType,
                InstanceCount = ca.InstanceCount,
                Interval = TimeSpan.FromMilliseconds(ca.Interval),
                InitialDelay = TimeSpan.FromSeconds(ca.InitialDelay),
                RunContinuous = ca.RunContinuously,
                NoServiceProvider = ca.NoServiceProvider
            });
        }

        private static void OutputHeader() {
            Console.WriteLine("Foundatio Job Runner v{0}", GetInformationalVersion());
            Console.WriteLine();
        }

        internal static string GetInformationalVersion() {
            return FileVersionInfo.GetVersionInfo(typeof(Program).Assembly.Location).ProductVersion;
        }
    }
}