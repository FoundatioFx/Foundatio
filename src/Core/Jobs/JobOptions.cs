using System;

namespace Foundatio.Jobs {
    public class JobOptions {
        public IJob Job { get; set; }
        public bool RunContinuous { get; set; } = true;
        public TimeSpan? Interval { get; set; }
        public TimeSpan? InitialDelay { get; set; }
        public int IterationLimit { get; set; } = -1;
        public int InstanceCount { get; set; } = 1;
    }
}