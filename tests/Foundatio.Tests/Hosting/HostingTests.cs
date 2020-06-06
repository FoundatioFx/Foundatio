using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.AsyncEx;
using Foundatio.Hosting.Startup;
using Foundatio.Xunit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Hosting {
    public class HostingTests : TestWithLoggingBase {
        public HostingTests(ITestOutputHelper output) : base(output) {}

        [Fact]
        public async Task WillRunSyncStartupAction() {
            var resetEvent = new AsyncManualResetEvent(false);
            var builder = new WebHostBuilder()
                .ConfigureServices(s => {
                    s.AddSingleton<ILoggerFactory>(Log);
                    s.AddStartupAction("Hey", () => resetEvent.Set());
                    s.AddHealthChecks().AddCheckForStartupActions("Critical");
                })
                .Configure(app => {
                    app.UseReadyHealthChecks("Critical");
                });
            
            var server = new TestServer(builder);
            
            await server.WaitForReadyAsync();
            await resetEvent.WaitAsync();
            
            var client = server.CreateClient();
            var response = await client.GetAsync("/ready");
            
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task WillRunAsyncStartupAction() {
            var resetEvent = new AsyncManualResetEvent(false);
            var builder = new WebHostBuilder()
                .ConfigureServices(s => {
                    s.AddSingleton<ILoggerFactory>(Log);
                    s.AddStartupAction("Hey", () => {
                        resetEvent.Set();
                        return Task.CompletedTask;
                    });
                    s.AddHealthChecks().AddCheckForStartupActions("Critical");
                })
                .Configure(app => {
                    app.UseReadyHealthChecks("Critical");
                });
            
            var server = new TestServer(builder);
            
            await server.WaitForReadyAsync();
            await resetEvent.WaitAsync();
            
            var client = server.CreateClient();
            var response = await client.GetAsync("/ready");
            
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task WillRunClassStartupAction() {
            var builder = new WebHostBuilder()
                .ConfigureServices(s => {
                    s.AddSingleton<ILoggerFactory>(Log);
                    s.AddStartupAction<TestStartupAction>("Hey");
                    s.AddHealthChecks().AddCheckForStartupActions("Critical");
                })
                .Configure(app => {
                    app.UseReadyHealthChecks("Critical");
                });
            
            var server = new TestServer(builder);
            
            await server.WaitForReadyAsync();
            Assert.True(TestStartupAction.HasRun);
            
            var client = server.CreateClient();
            var response = await client.GetAsync("/ready");
            
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        
        
        [Fact]
        public async Task WillStopWaitingWhenStartupActionFails() {
            var builder = new WebHostBuilder()
                .CaptureStartupErrors(true)
                .ConfigureServices(s => {
                    s.AddSingleton<ILoggerFactory>(Log);
                    s.AddStartupAction("Boom", () => throw new ApplicationException("Boom"));
                    s.AddHealthChecks().AddCheckForStartupActions("Critical");
                })
                .Configure(app => {
                    app.UseReadyHealthChecks("Critical");
                });
            
            var server = new TestServer(builder);

            var sw = Stopwatch.StartNew();
            await Assert.ThrowsAsync<OperationCanceledException>(() => server.WaitForReadyAsync());
            sw.Stop();
            
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
        }

    }
    
    public class TestStartupAction : IStartupAction {
        public static bool HasRun { get; private set; }

        public Task RunAsync(CancellationToken shutdownToken = default) {
            HasRun = true;
            return Task.CompletedTask;
        }
    }
}