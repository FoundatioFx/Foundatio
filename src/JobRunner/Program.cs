using System;
using System.Diagnostics;
using System.IO;
using CommandLine;

namespace Foundatio.JobRunner {
    internal class Program {
        private static int Main(string[] args) {
            int result = 0;
            try {
                var ca = new Options();
                if (!Parser.Default.ParseArguments(args, ca)) {
                    PauseIfDebug();
                    return 0;
                }

                var job = Jobs.JobRunner.CreateJobInstance(ca.JobType, ca.BootstrapperType);
                if (job == null)
                    return -1;

                result = Jobs.JobRunner.RunJob(job, ca.RunContinuously, ca.Quiet, ca.Delay, OutputHeader);
                PauseIfDebug();
            } catch (FileNotFoundException e) {
                Console.Error.WriteLine("{0} ({1})", e.Message, e.FileName);
                PauseIfDebug();
                return 1;
            } catch (Exception e) {
                Console.Error.WriteLine(e.ToString());
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