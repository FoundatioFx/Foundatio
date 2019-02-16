using System;
using Foundatio.Jobs;

namespace Foundatio.Hosting.Jobs {
    public class HostedJobOptionsBuilder<T> where T : IJob {
        public HostedJobOptionsBuilder(HostedJobOptions target = null) {
            Target = target ?? new HostedJobOptions();
        }

        public HostedJobOptions Target { get; }

        public HostedJobOptionsBuilder<T> ApplyDefaults() {
            Target.ApplyDefaults<T>();
            return this;
        }

        public HostedJobOptionsBuilder<T> Name(string value) {
            Target.Name = value;
            return this;
        }

        public HostedJobOptionsBuilder<T> Description(string value) {
            Target.Description = value;
            return this;
        }

        public HostedJobOptionsBuilder<T> JobFactory(Func<IJob> value) {
            Target.JobFactory = value;
            return this;
        }

        public HostedJobOptionsBuilder<T> RunContinuous(bool value) {
            Target.RunContinuous = value;
            return this;
        }

        public HostedJobOptionsBuilder<T> Interval(TimeSpan? value) {
            Target.Interval = value;
            return this;
        }

        public HostedJobOptionsBuilder<T> InitialDelay(TimeSpan? value) {
            Target.InitialDelay = value;
            return this;
        }

        public HostedJobOptionsBuilder<T> IterationLimit(int value) {
            Target.IterationLimit = value;
            return this;
        }

        public HostedJobOptionsBuilder<T> InstanceCount(int value) {
            Target.InstanceCount = value;
            return this;
        }

        public HostedJobOptionsBuilder<T> WaitForStartupActions(bool value) {
            Target.WaitForStartupActions = value;
            return this;
        }
    }
}