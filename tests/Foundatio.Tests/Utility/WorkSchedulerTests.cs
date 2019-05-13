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
            Log.MinimumLevel = LogLevel.Trace;
            using (TestSystemClock.Install(Log)) {
                var countdown = new CountdownEvent(1);
                SystemClock.ScheduleWork(() => {
                    _logger.LogTrace("Doing work");
                    countdown.Signal();
                }, TimeSpan.FromMinutes(5));
                WorkScheduler.Default.NoWorkItemsDue.WaitOne();
                Assert.Equal(1, countdown.CurrentCount);
                _logger.LogTrace("Adding 6 minutes to current time.");
                SystemClock.Sleep(TimeSpan.FromMinutes(6));
                WorkScheduler.Default.NoWorkItemsDue.WaitOne();
                countdown.Wait();
                Assert.Equal(0, countdown.CurrentCount);
            }
        }

        [Fact]
        public void CanScheduleMultipleUnorderedWorkItems() {
            Log.MinimumLevel = LogLevel.Trace;
            using (TestSystemClock.Install(Log)) {
                var countdown = new CountdownEvent(3);
                
                // schedule work due in 5 minutes
                SystemClock.ScheduleWork(() => {
                    _logger.LogTrace("Doing 5 minute work");
                    countdown.Signal();
                }, TimeSpan.FromMinutes(5));
                
                // schedule work due in 1 second
                SystemClock.ScheduleWork(() => {
                    _logger.LogTrace("Doing 1 second work");
                    countdown.Signal();
                }, TimeSpan.FromSeconds(1));
                
                // schedule work that is already past due
                SystemClock.ScheduleWork(() => {
                    _logger.LogTrace("Doing past due work");
                    countdown.Signal();
                }, TimeSpan.FromSeconds(-1));

                // wait until we get signal that no items are currently due
                _logger.LogTrace("Waiting for past due items to be started");
                Assert.True(WorkScheduler.Default.NoWorkItemsDue.WaitOne(), "Wait for all due work items to be scheduled");
                _logger.LogTrace("Waiting for past due work to be completed");
                // work can be done before we even get here, but wait one to be sure it's done
                countdown.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
                Assert.Equal(2, countdown.CurrentCount);
                
                // verify additional work will not happen until time changes
                Assert.False(countdown.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)));

                _logger.LogTrace("Adding 1 minute to current time");
                // sleeping for a minute to make 1 second work due
                SystemClock.Sleep(TimeSpan.FromMinutes(1));
                Assert.True(WorkScheduler.Default.NoWorkItemsDue.WaitOne());
                countdown.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
                Assert.Equal(1, countdown.CurrentCount);

                _logger.LogTrace("Adding 5 minutes to current time");
                // sleeping for 5 minutes to make 5 minute work due
                SystemClock.Sleep(TimeSpan.FromMinutes(5));
                _logger.LogTrace("Waiting for no work items due");
                Assert.True(WorkScheduler.Default.NoWorkItemsDue.WaitOne());
                _logger.LogTrace("Waiting for countdown to reach zero");
                Assert.True(countdown.Wait(TimeSpan.FromSeconds(1)));
                _logger.LogTrace("Check that current countdown is zero");
                Assert.Equal(0, countdown.CurrentCount);
            }
        }
        
        // long running work item won't block other work items from running
        // can run with no work scheduled
        // work items that throw don't affect other work items
        // do we need to do anything for unhandled exceptions or would users just use the normal unhandled exception handler since the tasks are just being run on the normal thread pool
    }
}
