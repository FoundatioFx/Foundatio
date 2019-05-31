using System;
using Microsoft.Extensions.Logging;

namespace Foundatio.Utility {
    public class TestSystemClock {
        public static void AddTime(TimeSpan amount) => TestSystemClockImpl.Instance.AddTime(amount);
        public static void SetTime(DateTime time, TimeSpan? timeZoneOffset = null) => TestSystemClockImpl.Instance.SetTime(time, timeZoneOffset);

        public static event EventHandler Changed {
            add => TestSystemClockImpl.Instance.Changed += value;
            remove => TestSystemClockImpl.Instance.Changed -= value;
        }

        public static ITestSystemClock Create(ILoggerFactory loggerFactory = null) {
            var testClock = new TestSystemClockImpl(SystemClock.Instance, loggerFactory);
            return testClock;
        }

        public static ITestSystemClock Install(ILoggerFactory loggerFactory = null) {
            var testClock = new TestSystemClockImpl(SystemClock.Instance, loggerFactory);
            SystemClock.SetInstance(testClock, loggerFactory);
            
            return testClock;
        }
    }
}