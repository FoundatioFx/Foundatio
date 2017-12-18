using System;
using System.Threading.Tasks;
using Foundatio.Logging.Xunit;
using Foundatio.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Utility {
    public class RunTests : TestWithLoggingBase {
        public RunTests(ITestOutputHelper output) : base(output) { }

        [Fact]
        public async Task CanRunWithRetries() {
            int count = 0;
            await Assert.ThrowsAsync<ApplicationException>(async () => await Run.WithRetriesAsync(() => {
                count++;
                throw new ApplicationException();
            }, 5));
            
            Assert.Equal(5, count);
        }

        [Fact]
        public async Task CanRunDelayed() {
            var start = SystemClock.Now;
            TimeSpan duration = TimeSpan.Zero;
            await Run.DelayedRunAsync(TimeSpan.FromMilliseconds(100), () => {
                duration = SystemClock.Now.Subtract(start);
                return Task.CompletedTask;
            });
            Assert.True(duration > TimeSpan.FromMilliseconds(60));

        }
    }
}