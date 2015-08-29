using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Extensions;
using Foundatio.Lock;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests {
    public class ThrottlingLockTests : LockTestBase {
        private readonly TimeSpan _period = TimeSpan.FromSeconds(1);

        public ThrottlingLockTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) {}

        protected override ILockProvider GetLockProvider() {
            return new ThrottlingLockProvider(new InMemoryCacheClient(), 5, _period);
        }

        [Fact]
        public async Task WillThrottleCalls() {
            var locker = GetLockProvider();
            if (locker == null)
                return;

            // sleep until start of throttling period
            Thread.Sleep(DateTime.UtcNow.Ceiling(_period) - DateTime.UtcNow);
            var sw = new Stopwatch();
            sw.Start();
            for (int i = 0; i < 5; i++)
                await locker.AcquireLockAsync("test").AnyContext();
            sw.Stop();

            _output.WriteLine(sw.Elapsed.ToString());
            Assert.True(sw.Elapsed.TotalSeconds < 1);

            sw.Reset();
            sw.Start();
            var result = await locker.AcquireLockAsync("test", acquireTimeout: TimeSpan.Zero).AnyContext();
            sw.Stop();
            Assert.Null(result);
            _output.WriteLine(sw.Elapsed.ToString());

            sw.Reset();
            sw.Start();
            result = await locker.AcquireLockAsync("test", acquireTimeout: TimeSpan.FromMilliseconds(250)).AnyContext();
            sw.Stop();
            Assert.Null(result);
            _output.WriteLine(sw.Elapsed.ToString());

            sw.Reset();
            sw.Start();
            result = await locker.AcquireLockAsync("test", acquireTimeout: TimeSpan.FromSeconds(2)).AnyContext();
            sw.Stop();
            Assert.NotNull(result);
            _output.WriteLine(sw.Elapsed.ToString());
        }
    }
}