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
                TestSystemClock.Freeze();
                var workScheduler = new WorkScheduler(Log);
                var countdown = new CountdownEvent(1);
                workScheduler.Schedule(() => {
                    _logger.LogTrace("Doing work");
                    countdown.Signal();
                }, TimeSpan.FromMinutes(5));
                workScheduler.NoWorkItemsDue.WaitOne();
                Assert.Equal(1, countdown.CurrentCount);
                _logger.LogTrace("Adding 6 minutes to current time.");
                TestSystemClock.AddTime(TimeSpan.FromMinutes(6));
                workScheduler.NoWorkItemsDue.WaitOne();
                countdown.Wait();
                Assert.Equal(0, countdown.CurrentCount);
            }
        }

        [Fact]
        public void CanScheduleMultipleWorkItems() {
            using (TestSystemClock.Install()) {
                Log.MinimumLevel = LogLevel.Trace;
                var now = TestSystemClock.Freeze();
                var workScheduler = new WorkScheduler(Log);
                var countdown = new CountdownEvent(3);
                
                // schedule work due in 5 minutes
                workScheduler.Schedule(() => {
                    _logger.LogTrace("Doing 5 minute work");
                    countdown.Signal();
                }, TimeSpan.FromMinutes(5));
                
                // schedule work due in 1 second
                workScheduler.Schedule(() => {
                    _logger.LogTrace("Doing 1 second work");
                    countdown.Signal();
                }, TimeSpan.FromSeconds(1));
                
                // schedule work that is already past due
                workScheduler.Schedule(() => {
                    _logger.LogTrace("Doing past due work");
                    countdown.Signal();
                }, TimeSpan.FromSeconds(-1));

                // wait until we get signal that no items are currently due
                workScheduler.NoWorkItemsDue.WaitOne();
                countdown.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
                Assert.Equal(2, countdown.CurrentCount);
                
                // verify additional work will not happen until time changes
                Assert.False(countdown.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)));

                _logger.LogTrace("Adding 1 minute to current time.");
                TestSystemClock.AddTime(TimeSpan.FromMinutes(1));
                workScheduler.NoWorkItemsDue.WaitOne();
                countdown.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
                Assert.Equal(1, countdown.CurrentCount);

                _logger.LogTrace("Adding 5 minutes to current time.");
                TestSystemClock.AddTime(TimeSpan.FromMinutes(5));
                workScheduler.NoWorkItemsDue.WaitOne();
                countdown.Wait(TimeSpan.FromSeconds(1));
                Assert.Equal(0, countdown.CurrentCount);
            }
        }
        // long running work item won't block other work items from running
    }
}
