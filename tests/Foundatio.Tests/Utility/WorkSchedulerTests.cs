using System;
using System.Threading;
using Foundatio.AsyncEx;
using Foundatio.Logging.Xunit;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Utility {
    public class WorkSchedulerTests : TestWithLoggingBase {
        public WorkSchedulerTests(ITestOutputHelper output) : base(output) { }

        [Fact]
        public void CanScheduleWork() {
            using (TestSystemClock.Install()) {
                Log.MinimumLevel = LogLevel.Trace;
                var now = TestSystemClock.Freeze();
                var workScheduler = new WorkScheduler(Log);
                bool workCompleted = false;
                var countdown = new AsyncCountdownEvent(1);
                workScheduler.Schedule(() => { workCompleted = true; }, TimeSpan.FromMinutes(5));
                Thread.Sleep(1000);
                Assert.False(workCompleted);
                TestSystemClock.AddTime(TimeSpan.FromMinutes(6));
                Thread.Sleep(1000);
                Assert.True(workCompleted);
            }
        }

        // long running work item won't block other work items from running
        // make sure inserting work items with out of order due times works
    }
}
