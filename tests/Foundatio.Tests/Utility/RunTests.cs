using System.Collections.Generic;
using Foundatio.Xunit;
using Foundatio.Utility;
using Xunit;
using Xunit.Abstractions;
using System.Threading.Tasks;
using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Foundatio.Tests.Utility {
    public class RunTests : TestWithLoggingBase {
        public RunTests(ITestOutputHelper output) : base(output) { }

        [Fact]
        public async Task CanRunWithRetries() {
            var task = Task.Run(() => {
                _logger.LogInformation("Hi");
            });

            await task;
            await task;

            await Run.WithRetriesAsync(() => {
                return DoStuff();
            }, maxAttempts: 5, retryInterval: TimeSpan.FromMilliseconds(10), cancellationToken: CancellationToken.None, logger: _logger);

            await Run.WithRetriesAsync(async () => {
                await DoStuff();
            }, maxAttempts: 5, retryInterval: TimeSpan.FromMilliseconds(10), cancellationToken: CancellationToken.None, logger: _logger);
        }

        [Fact]
        public async Task CanRunWithRetriesAndResult() {
            var result = await Run.WithRetriesAsync(() => {
                return ReturnStuff();
            }, maxAttempts: 5, retryInterval: TimeSpan.FromMilliseconds(10), cancellationToken: CancellationToken.None, logger: _logger);

            Assert.Equal(1, result);

            result = await Run.WithRetriesAsync(async () => {
                return await ReturnStuff();
            }, maxAttempts: 5, retryInterval: TimeSpan.FromMilliseconds(10), cancellationToken: CancellationToken.None, logger: _logger);

            Assert.Equal(1, result);
        }

        [Fact]
        public async Task CanBoomWithRetries() {
            var exception = await Assert.ThrowsAsync<ApplicationException>(async () => {
                await Run.WithRetriesAsync(() => {
                    return DoBoom();
                }, maxAttempts: 5, retryInterval: TimeSpan.FromMilliseconds(10), cancellationToken: CancellationToken.None, logger: _logger);
            });
            Assert.Equal("Hi", exception.Message);

            exception = await Assert.ThrowsAsync<ApplicationException>(async () => {
                await Run.WithRetriesAsync(async () => {
                    await DoBoom();
                }, maxAttempts: 5, retryInterval: TimeSpan.FromMilliseconds(10), cancellationToken: CancellationToken.None, logger: _logger);
            });
            Assert.Equal("Hi", exception.Message);

            int attempt = 0;
            await Run.WithRetriesAsync(() => {
                attempt++;
                return DoBoom(attempt < 5);
            }, maxAttempts: 5, retryInterval: TimeSpan.FromMilliseconds(10), cancellationToken: CancellationToken.None, logger: _logger);
            Assert.Equal(5, attempt);

            attempt = 0;
            await Run.WithRetriesAsync(async () => {
                attempt++;
                await DoBoom(attempt < 5);
            }, maxAttempts: 5, retryInterval: TimeSpan.FromMilliseconds(10), cancellationToken: CancellationToken.None, logger: _logger);
            Assert.Equal(5, attempt);
        }

        [Fact]
        public async Task CanBoomWithRetriesAndResult() {
            var exception = await Assert.ThrowsAsync<ApplicationException>(async () => {
                var result = await Run.WithRetriesAsync(() => {
                    return ReturnBoom();
                }, maxAttempts: 5, retryInterval: TimeSpan.FromMilliseconds(10), cancellationToken: CancellationToken.None, logger: _logger);

                Assert.Equal(1, result);
            });
            Assert.Equal("Hi", exception.Message);

            exception = await Assert.ThrowsAsync<ApplicationException>(async () => {
                var result = await Run.WithRetriesAsync(async () => {
                    return await ReturnBoom();
                }, maxAttempts: 5, retryInterval: TimeSpan.FromMilliseconds(10), cancellationToken: CancellationToken.None, logger: _logger);

                Assert.Equal(1, result);
            });
            Assert.Equal("Hi", exception.Message);

            int attempt = 0;
            var result = await Run.WithRetriesAsync(() => {
                attempt++;
                return ReturnBoom(attempt < 5);
            }, maxAttempts: 5, retryInterval: TimeSpan.FromMilliseconds(10), cancellationToken: CancellationToken.None, logger: _logger);
            Assert.Equal(5, attempt);
            Assert.Equal(1, result);

            attempt = 0;
            result = await Run.WithRetriesAsync(async () => {
                attempt++;
                return await ReturnBoom(attempt < 5);
            }, maxAttempts: 5, retryInterval: TimeSpan.FromMilliseconds(10), cancellationToken: CancellationToken.None, logger: _logger);
            Assert.Equal(5, attempt);
            Assert.Equal(1, result);
        }

        private async Task<int> ReturnStuff() {
            await Task.Delay(10);
            return 1;
        }

        private Task DoStuff() {
            return Task.Delay(10);
        }

        private async Task<int> ReturnBoom(bool shouldThrow = true) {
            await Task.Delay(10);

            if (shouldThrow)
                throw new ApplicationException("Hi");

            return 1;
        }

        private async Task DoBoom(bool shouldThrow = true) {
            await Task.Delay(10);

            if (shouldThrow)
                throw new ApplicationException("Hi");
        }
    }
}