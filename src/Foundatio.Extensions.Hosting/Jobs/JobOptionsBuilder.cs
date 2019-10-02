using System;
using Foundatio.Jobs;

namespace Foundatio.Extensions.Hosting.Jobs {
    public class HostedJobOptionsBuilder {
        public HostedJobOptionsBuilder(HostedJobOptions target = null) {
            Target = target ?? new HostedJobOptions();
        }

        public HostedJobOptions Target { get; }
        
        public HostedJobOptionsBuilder ApplyDefaults<T>() where T: IJob {
            Target.ApplyDefaults<T>();
            return this;
        }
        
        public HostedJobOptionsBuilder ApplyDefaults(Type jobType) {
            JobOptions.ApplyDefaults(Target, jobType);
            return this;
        }

        public HostedJobOptionsBuilder Name(string value) {
            Target.Name = value;
            return this;
        }

        public HostedJobOptionsBuilder Description(string value) {
            Target.Description = value;
            return this;
        }

        public HostedJobOptionsBuilder JobFactory(Func<IJob> value) {
            Target.JobFactory = value;
            return this;
        }

        public HostedJobOptionsBuilder RunContinuous(bool value) {
            Target.RunContinuous = value;
            return this;
        }

        public HostedJobOptionsBuilder CronSchedule(string value) {
            Target.CronSchedule = value;
            return this;
        }

        public HostedJobOptionsBuilder Interval(TimeSpan? value) {
            Target.Interval = value;
            return this;
        }

        public HostedJobOptionsBuilder InitialDelay(TimeSpan? value) {
            Target.InitialDelay = value;
            return this;
        }

        public HostedJobOptionsBuilder IterationLimit(int value) {
            Target.IterationLimit = value;
            return this;
        }

        public HostedJobOptionsBuilder InstanceCount(int value) {
            Target.InstanceCount = value;
            return this;
        }

        public HostedJobOptionsBuilder WaitForStartupActions(bool value) {
            Target.WaitForStartupActions = value;
            return this;
        }
    }
}