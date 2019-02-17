using System;
using Foundatio.Jobs;

namespace Foundatio.Hosting.Jobs {
    public class ScheduledJobRegistration {
        public ScheduledJobRegistration(Func<IJob> jobFactory, string schedule) {
            JobFactory = jobFactory;
            Schedule = schedule;
        }

        public Func<IJob> JobFactory { get; }
        public string Schedule { get; }
    }
}
