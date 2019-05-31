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
            _logger.LogTrace("Starting test on thread {ThreadId} time {Time}", Thread.CurrentThread.ManagedThreadId, DateTime.Now);
            using (var clock = TestSystemClock.Install(Log)) {
                var countdown = new CountdownEvent(1);
                SystemClock.ScheduleWork(() => {
                    _logger.LogTrace("Doing work");
                    countdown.Signal();
                }, TimeSpan.FromMinutes(5));
                clock.NoScheduledWorkItemsDue.WaitOne(TimeSpan.FromMilliseconds(100));
                Assert.Equal(1, countdown.CurrentCount);
                _logger.LogTrace("Adding 6 minutes to current time.");
                SystemClock.Sleep(TimeSpan.FromMinutes(6));
                clock.NoScheduledWorkItemsDue.WaitOne(TimeSpan.FromMilliseconds(100));
                countdown.Wait();
                Assert.Equal(0, countdown.CurrentCount);
            }
            _logger.LogTrace("Ending test on thread {ThreadId} time {Time}", Thread.CurrentThread.ManagedThreadId, DateTime.Now);
        }

        [Fact]
        public void CanScheduleMultipleUnorderedWorkItems() {
            Log.MinimumLevel = LogLevel.Trace;
            _logger.LogTrace("Starting test on thread {ThreadId} time {Time}", Thread.CurrentThread.ManagedThreadId, DateTime.Now);
            using (var clock = TestSystemClock.Install(Log)) {
                var work1Event = new ManualResetEvent(false);
                var work2Event = new ManualResetEvent(false);
                var work3Event = new ManualResetEvent(false);
                
                // schedule work due in 5 minutes
                clock.ScheduleWork(() => {
                    _logger.LogTrace("Doing 5 minute work");
                    work1Event.Set();
                }, TimeSpan.FromMinutes(5));
                
                // schedule work due in 1 second
                clock.ScheduleWork(() => {
                    _logger.LogTrace("Doing 1 second work");
                    work2Event.Set();
                }, TimeSpan.FromSeconds(1));
                
                // schedule work that is already past due
                clock.ScheduleWork(() => {
                    _logger.LogTrace("Doing past due work");
                    work3Event.Set();
                }, TimeSpan.FromSeconds(-1));

                // wait until we get signal that no items are currently due
                _logger.LogTrace("Waiting for past due items to be started");
                Assert.True(clock.NoScheduledWorkItemsDue.WaitOne(), "Wait for all due work items to be scheduled");
                _logger.LogTrace("Waiting for past due work to be completed");
                // work can be done before we even get here, but wait one to be sure it's done
                Assert.True(work3Event.WaitOne(TimeSpan.FromSeconds(1)));

                // verify additional work will not happen until time changes
                Assert.False(work2Event.WaitOne(TimeSpan.FromMilliseconds(100)));

                _logger.LogTrace("Adding 1 minute to current time");
                // sleeping for a minute to make 1 second work due
                clock.Sleep(TimeSpan.FromMinutes(1));
                Assert.True(clock.NoScheduledWorkItemsDue.WaitOne());
                Assert.True(work2Event.WaitOne(TimeSpan.FromSeconds(1)));

                _logger.LogTrace("Adding 5 minutes to current time");
                // sleeping for 5 minutes to make 5 minute work due
                clock.Sleep(TimeSpan.FromMinutes(5));
                _logger.LogTrace("Waiting for no work items due");
                Assert.True(clock.NoScheduledWorkItemsDue.WaitOne());
                _logger.LogTrace("Waiting for 5 minute work to be completed");
                Assert.True(work1Event.WaitOne(TimeSpan.FromSeconds(1)));
            }
            _logger.LogTrace("Ending test on thread {ThreadId} time {Time}", Thread.CurrentThread.ManagedThreadId, DateTime.Now);
        }
        
        // long running work item won't block other work items from running, this is bad usage, but we should make sure it works.
        // can run with no work scheduled
        // work items that throw don't affect other work items
        // do we need to do anything for unhandled exceptions or would users just use the normal unhandled exception handler since the tasks are just being run on the normal thread pool
        // test overall performance, what is the throughput of this scheduler? Should be good since it's just using the normal thread pool, but we may want to increase max concurrent or base it on ThreadPool.GetMaxThreads to avoid thread starvation solely coming from Foundatio.
        // verify multiple tests manipulating the systemclock don't affect each other
    }
}
