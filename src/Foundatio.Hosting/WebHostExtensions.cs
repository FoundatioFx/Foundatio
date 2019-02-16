using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Foundatio.Hosting {
    public static class WebHostExtensions {
        /// <summary>
        /// Block the calling thread until shutdown is triggered via Ctrl+C or SIGTERM.
        /// </summary>
        /// <param name="host">The running <see cref="IWebHost"/>.</param>
        /// <param name="logger">The logger to log status messages to.</param>
        public static void WaitForShutdown(this IWebHost host, ILogger logger) {
            host.WaitForShutdownAsync(logger).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Returns a Task that completes when shutdown is triggered via the given token, Ctrl+C or SIGTERM.
        /// </summary>
        /// <param name="host">The running <see cref="IWebHost"/>.</param>
        /// <param name="logger">The logger to log status messages to.</param>
        /// <param name="token">The token to trigger shutdown.</param>
        public static async Task WaitForShutdownAsync(this IWebHost host, ILogger logger, CancellationToken token = default) {
            var done = new ManualResetEventSlim(false);
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token)) {
                AttachCtrlcSigtermShutdown(cts, done, logger, shutdownMessage: String.Empty);

                try {
                    await host.WaitForTokenShutdownAsync(cts.Token);
                } finally {
                    done.Set();
                }
            }
        }

        /// <summary>
        /// Runs a web application and block the calling thread until host shutdown.
        /// </summary>
        /// <param name="host">The <see cref="IWebHost"/> to run.</param>
        /// <param name="logger">The logger to log status messages to.</param>
        public static void Run(this IWebHost host, ILogger logger) {
            host.RunAsync(logger).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Runs a web application and returns a Task that only completes when the token is triggered or shutdown is triggered.
        /// </summary>
        /// <param name="host">The <see cref="IWebHost"/> to run.</param>
        /// <param name="token">The token to trigger shutdown.</param>
        /// <param name="logger">The logger to log status messages to.</param>
        public static async Task RunAsync(this IWebHost host, ILogger logger, CancellationToken token = default) {
            // Wait for token shutdown if it can be canceled
            if (token.CanBeCanceled) {
                await host.RunAsync(logger, token, shutdownMessage: null);
                return;
            }

            // If token cannot be canceled, attach Ctrl+C and SIGTERM shutdown
            var done = new ManualResetEventSlim(false);
            using (var cts = new CancellationTokenSource()) {
                AttachCtrlcSigtermShutdown(cts, done, logger, shutdownMessage: "Application is shutting down...");

                try {
                    await host.RunAsync(logger, cts.Token, "Application started. Press Ctrl+C to shut down.");
                } finally {
                    done.Set();
                }
            }
        }

        private static async Task RunAsync(this IWebHost host, ILogger logger, CancellationToken token, string shutdownMessage) {
            using (host) {
                await host.StartAsync(token);

                var hostingEnvironment = host.Services.GetService<IHostingEnvironment>();

                logger.LogInformation("Hosting environment: {EnvironmentName}", hostingEnvironment.EnvironmentName);
                logger.LogInformation("Content root path: {ContentRootPath}", hostingEnvironment.ContentRootPath);

                var serverAddresses = host.ServerFeatures.Get<IServerAddressesFeature>()?.Addresses;
                if (serverAddresses != null)
                    foreach (var address in serverAddresses)
                        logger.LogInformation("Now listening on: {Address}", address);

                if (!string.IsNullOrEmpty(shutdownMessage))
                    logger.LogInformation(shutdownMessage);

                await host.WaitForTokenShutdownAsync(token);
            }
        }

        private static void AttachCtrlcSigtermShutdown(CancellationTokenSource cts, ManualResetEventSlim resetEvent, ILogger logger, string shutdownMessage) {
            void Shutdown() {
                if (!cts.IsCancellationRequested) {
                    if (!string.IsNullOrEmpty(shutdownMessage)) {
                        logger.LogInformation(shutdownMessage);
                    }
                    try {
                        cts.Cancel();
                    } catch (ObjectDisposedException) { }
                }

                // Wait on the given reset event
                resetEvent.Wait();
            };

            AppDomain.CurrentDomain.ProcessExit += (sender, eventArgs) => Shutdown();
            Console.CancelKeyPress += (sender, eventArgs) => {
                Shutdown();
                // Don't terminate the process immediately, wait for the Main thread to exit gracefully.
                eventArgs.Cancel = true;
            };
        }

        private static async Task WaitForTokenShutdownAsync(this IWebHost host, CancellationToken token) {
            var applicationLifetime = host.Services.GetService<IApplicationLifetime>();

            token.Register(state => {
                ((IApplicationLifetime)state).StopApplication();
            },
            applicationLifetime);

            var waitForStop = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            applicationLifetime.ApplicationStopping.Register(obj => {
                var tcs = (TaskCompletionSource<object>)obj;
                tcs.TrySetResult(null);
            }, waitForStop);

            await waitForStop.Task;

            // WebHost will use its default ShutdownTimeout if none is specified.
            await host.StopAsync();
        }
    }
}