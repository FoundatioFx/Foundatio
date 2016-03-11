using System;

namespace Foundatio.Jobs {
    public class JobOptions {
        public IJob JobInstance { get; set; }
        public string JobTypeName { get; set; }
        public Type JobType { get; set; }
        public string ServiceProviderTypeName { get; set; }
        public Type ServiceProviderType { get; set; }
        public bool RunContinuous { get; set; } = true;
        public TimeSpan? Interval { get; set; }
        public TimeSpan? InitialDelay { get; set; }
        public int IterationLimit { get; set; } = -1;
        public int InstanceCount { get; set; } = 1;
        public bool? NoServiceProvider { get; set; }

        public JobOptions Clone() {
            return new JobOptions {
                JobInstance = JobInstance,
                JobTypeName = JobTypeName,
                JobType = JobType,
                ServiceProviderType = ServiceProviderType,
                ServiceProviderTypeName = ServiceProviderTypeName,
                RunContinuous = RunContinuous,
                Interval = Interval,
                InitialDelay = InitialDelay,
                IterationLimit = IterationLimit,
                InstanceCount = InstanceCount,
                NoServiceProvider = NoServiceProvider
            };
        }
    }
}