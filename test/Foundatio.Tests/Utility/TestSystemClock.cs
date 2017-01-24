﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Concurrency;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.Reactive.Testing;

namespace Foundatio.Tests.Utility
{
    public sealed class TestSystemClock: SchedulerSystemClockBase
    {
        public static TestSystemClock Instance {
            get
            {
                var result = SystemClock.Instance as TestSystemClock;
                if (result == null)
                    throw new InvalidOperationException("Must call Install() before accessing Instance.");
                return result;
            }
        }

        public static IDisposable Install() {
            var clock = new TestSystemClock();
            clock.Scheduler.AdvanceTo(DateTimeOffset.UtcNow.Ticks);
            return SystemClock.SetInstance(clock);
        }

        private TestSystemClock(TestScheduler scheduler)
            : base(scheduler)
        {
            Scheduler = scheduler;
        }

        public TestSystemClock(): this(new TestScheduler()) { }

        public TestScheduler Scheduler { get; }

        public override CancellationTokenSource CreateCancellationTokenSource(TimeSpan timeout)
        {
            var result = new CancellationTokenSource();
            Scheduler.Schedule(timeout, () => result.Cancel());
            return result;
        }

        public static void AdvanceBy(TimeSpan timeSpan)
        {
            if (SystemClock.Instance is TestSystemClock)
                Instance.Scheduler.AdvanceBy(timeSpan.Ticks);
            else
                SystemClock.Sleep(timeSpan);
        }

        public override string ToString()
        {
            return Scheduler.Clock + "(" + Scheduler.Now + ")";
        }
    }
}
