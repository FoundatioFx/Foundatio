using System;
using CommandLine;
using CommandLine.Text;

namespace Foundatio.JobRunner {
    public class Options {
        [Option('t', "jobtype", Required = true, HelpText = "The type of job that you wish to run.")]
        public string JobType { get; set; }

        [Option('s', "serviceprovider", Required = false, HelpText = "The type to be used to create job instances and resolve service dependencies.")]
        public string ServiceProviderType { get; set; }

        [Option('n', "noserviceprovider", Required = false, HelpText = "Don't use a service provider.")]
        public bool? NoServiceProvider { get; set; }

        [Option('c', "continuous", Required = false, DefaultValue = false, HelpText = "Run the job in a continuous loop.")]
        public bool RunContinuously { get; set; }

        [Option('q', "quiet", Required = false, DefaultValue = false, HelpText = "Don't output header text.")]
        public bool Quiet { get; set; }

        [Option('d', "delay", Required = false, DefaultValue = 0, HelpText = "Amount of time in milliseconds to delay between continuous job runs.")]
        public int Delay { get; set; }

        [Option('i', "instances", Required = false, DefaultValue = 1, HelpText = "Number of concurrent job instances to run. Only applies to continuous jobs.")]
        public int InstanceCount { get; set; }

        [HelpOption]
        public string GetUsage() {
            var help = new HelpText {
                Heading = String.Format("Foundatio Job Runner v{0}", Program.GetInformationalVersion()),
                AdditionalNewLineAfterOption = false,
                AddDashesToOption = true
            };
            
            help.AddPreOptionsLine(" ");
            help.AddPreOptionsLine("Example usage: job -t \"MyAssembly.MyJobType, MyAssembly\" -c");
            help.AddOptions(this);

            return help;
        }
    }
}