using System;

namespace Foundatio.Jobs {
    public class JobOptions {
        public IJob Job { get; set; }
        public bool RunContinuous { get; set; } = true;
        public TimeSpan? Interval { get; set; }
        public TimeSpan? InitialDelay { get; set; }
        public int IterationLimit { get; set; } = -1;
        public int InstanceCount { get; set; } = 1;

        public JobOptions Clone() {
            return new JobOptions {
                Job = Job,
                RunContinuous = RunContinuous,
                Interval = Interval,
                InitialDelay = InitialDelay,
                IterationLimit = IterationLimit,
                InstanceCount = InstanceCount
            };
        }
    }
}