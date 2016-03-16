using System;

namespace Foundatio.Jobs {
    public class JobOptions {
        public Func<IJob> JobFactory { get; set; }
        public bool RunContinuous { get; set; } = true;
        public TimeSpan? Interval { get; set; }
        public TimeSpan? InitialDelay { get; set; }
        public int IterationLimit { get; set; } = -1;
        public int InstanceCount { get; set; } = 1;
    }
}