using System;
using System.Diagnostics;
using System.IO;
using CommandLine;
using Foundatio.Jobs;
using NLog.Fluent;

namespace Foundatio.JobRunner {
    internal class Program {
        private static int Main(string[] args) {
            int result;
            try {
                var ca = new Options();
                if (!Parser.Default.ParseArguments(args, ca)) {
                    PauseIfDebug();
                    return 0;
                }

                if (!ca.Quiet)
                    OutputHeader();
                
                result = Jobs.JobRunner.RunAsync(new JobRunOptions {
                    JobTypeName = ca.JobType,
                    ServiceProviderTypeName = ca.ServiceProviderType,
                    InstanceCount = ca.InstanceCount,
                    Interval = TimeSpan.FromMilliseconds(ca.Delay),
                    RunContinuous = ca.RunContinuously
                }).Result;

                PauseIfDebug();
            } catch (FileNotFoundException e) {
                Console.Error.WriteLine("{0} ({1})", e.Message, e.FileName);
                Log.Error().Message("{0} ({1})", e.Message, e.FileName).Write();

                PauseIfDebug();
                return 1;
            } catch (Exception e) {
                Console.Error.WriteLine(e.ToString());
                Log.Error().Message("{0} ({1})", e.ToString()).Write();

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