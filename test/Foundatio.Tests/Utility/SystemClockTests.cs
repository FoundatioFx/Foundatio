using System;
using System.Threading;
using Foundatio.Utility;
using Xunit;

namespace Foundatio.Tests.Utility {
    public class SystemClockTests {
        [Fact]
        public void CanGetTime() {
            Assert.InRange(DateTime.UtcNow.Subtract(SystemClock.UtcNow).TotalMilliseconds, -50, 50);
            Assert.InRange(DateTime.Now.Subtract(SystemClock.Now).TotalMilliseconds, -50, 50);
            Assert.InRange(DateTimeOffset.UtcNow.Subtract(SystemClock.OffsetUtcNow).TotalMilliseconds, -50, 50);
            Assert.InRange(DateTimeOffset.Now.Subtract(SystemClock.OffsetNow).TotalMilliseconds, -50, 50);
            Assert.InRange(DateTimeOffset.Now.Offset.Subtract(SystemClock.TimeZoneOffset).TotalMilliseconds, -50, 50);
            Assert.Equal(DateTimeOffset.Now.Offset, SystemClock.TimeZoneOffset);
        }

        [Fact]
        public void CanSetTimeZone() {
            var utcNow = DateTime.UtcNow;
            var now = new DateTime(utcNow.AddHours(1).Ticks, DateTimeKind.Local);
            SystemClock.SetFixedTime(utcNow);
            SystemClock.SetTimeZoneOffset(TimeSpan.FromHours(1));

            Assert.Equal(utcNow, SystemClock.UtcNow);
            Assert.Equal(utcNow, SystemClock.OffsetUtcNow);
            Assert.Equal(now, SystemClock.Now);
            Assert.Equal(new DateTimeOffset(now.Ticks, TimeSpan.FromHours(1)), SystemClock.OffsetNow);
            Assert.Equal(TimeSpan.FromHours(1), SystemClock.TimeZoneOffset);
        }

        [Fact]
        public void CanSetLocalFixedTime() {
            var now = DateTime.Now;
            var utcNow = now.ToUniversalTime();
            SystemClock.SetFixedTime(now);

            Assert.Equal(now, SystemClock.Now);
            Assert.Equal(now, SystemClock.OffsetNow);
            Assert.Equal(utcNow, SystemClock.UtcNow);
            Assert.Equal(utcNow, SystemClock.OffsetUtcNow);
            Assert.Equal(DateTimeOffset.Now.Offset, SystemClock.TimeZoneOffset);
        }

        [Fact]
        public void CanSetUtcFixedTime() {
            var utcNow = DateTime.UtcNow;
            var now = utcNow.ToLocalTime();
            SystemClock.SetFixedTime(utcNow);

            Assert.Equal(now, SystemClock.Now);
            Assert.Equal(now, SystemClock.OffsetNow);
            Assert.Equal(utcNow, SystemClock.UtcNow);
            Assert.Equal(utcNow, SystemClock.OffsetUtcNow);
            Assert.Equal(DateTimeOffset.Now.Offset, SystemClock.TimeZoneOffset);
        }

        [Fact]
        public void CanSetLocalTime() {
            SystemClock.SetTime(DateTime.Now.AddMinutes(-5));

            Assert.InRange(DateTime.UtcNow.AddMinutes(-5).Subtract(SystemClock.UtcNow).TotalMilliseconds, -50, 50);
            Assert.InRange(DateTime.Now.AddMinutes(-5).Subtract(SystemClock.Now).TotalMilliseconds, -50, 50);
            Assert.InRange(new DateTimeOffset(DateTime.UtcNow.AddMinutes(-5).Ticks, TimeSpan.Zero).Subtract(SystemClock.OffsetUtcNow).TotalMilliseconds, -50, 50);
            Assert.InRange(new DateTimeOffset(DateTime.Now.AddMinutes(-5).Ticks, SystemClock.TimeZoneOffset).Subtract(SystemClock.OffsetNow).TotalMilliseconds, -50, 50);
            Assert.Equal(DateTimeOffset.Now.Offset, SystemClock.TimeZoneOffset);

            Thread.Sleep(500);

            Assert.InRange(DateTime.UtcNow.AddMinutes(-5).Subtract(SystemClock.UtcNow).TotalMilliseconds, -50, 50);
            Assert.InRange(DateTime.Now.AddMinutes(-5).Subtract(SystemClock.Now).TotalMilliseconds, -50, 50);
            Assert.InRange(new DateTimeOffset(DateTime.UtcNow.AddMinutes(-5).Ticks, TimeSpan.Zero).Subtract(SystemClock.OffsetUtcNow).TotalMilliseconds, -50, 50);
            Assert.InRange(new DateTimeOffset(DateTime.Now.AddMinutes(-5).Ticks, SystemClock.TimeZoneOffset).Subtract(SystemClock.OffsetNow).TotalMilliseconds, -50, 50);
            Assert.Equal(DateTimeOffset.Now.Offset, SystemClock.TimeZoneOffset);
        }

        [Fact]
        public void CanSetUtcTime() {
            SystemClock.SetTime(DateTime.UtcNow.AddMinutes(-5));

            Assert.InRange(DateTime.UtcNow.AddMinutes(-5).Subtract(SystemClock.UtcNow).TotalMilliseconds, -50, 50);
            Assert.InRange(DateTime.Now.AddMinutes(-5).Subtract(SystemClock.Now).TotalMilliseconds, -50, 50);
            Assert.InRange(new DateTimeOffset(DateTime.UtcNow.AddMinutes(-5).Ticks, TimeSpan.Zero).Subtract(SystemClock.OffsetUtcNow).TotalMilliseconds, -50, 50);
            Assert.InRange(new DateTimeOffset(DateTime.Now.AddMinutes(-5).Ticks, SystemClock.TimeZoneOffset).Subtract(SystemClock.OffsetNow).TotalMilliseconds, -50, 50);
            Assert.Equal(DateTimeOffset.Now.Offset, SystemClock.TimeZoneOffset);

            Thread.Sleep(500);

            Assert.InRange(DateTime.UtcNow.AddMinutes(-5).Subtract(SystemClock.UtcNow).TotalMilliseconds, -50, 50);
            Assert.InRange(DateTime.Now.AddMinutes(-5).Subtract(SystemClock.Now).TotalMilliseconds, -50, 50);
            Assert.InRange(new DateTimeOffset(DateTime.UtcNow.AddMinutes(-5).Ticks, TimeSpan.Zero).Subtract(SystemClock.OffsetUtcNow).TotalMilliseconds, -50, 50);
            Assert.InRange(new DateTimeOffset(DateTime.Now.AddMinutes(-5).Ticks, SystemClock.TimeZoneOffset).Subtract(SystemClock.OffsetNow).TotalMilliseconds, -50, 50);
            Assert.Equal(DateTimeOffset.Now.Offset, SystemClock.TimeZoneOffset);
        }
    }
}
