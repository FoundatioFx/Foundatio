using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Concurrency;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Reactive.Testing;

namespace Foundatio.Tests.Utility
{
    public sealed class TestSystemClock: SchedulerSystemClockBase
    {
        public static TestSystemClock Instance { get; private set; }

        public static void Install()
        {
            SystemClock.Instance = Instance = new TestSystemClock();
        }

        private TestSystemClock(TestScheduler scheduler)
            : base(scheduler)
        {
            Scheduler = scheduler;
        }

        public TestSystemClock(): this(new TestScheduler()) { }

        public TestScheduler Scheduler { get; }
    }
}
