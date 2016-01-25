using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Foundatio.Queues;
using Foundatio.Tests.Queue;
using Foundatio.Tests.Utility;
using Xunit;
using System.Threading.Tasks;
using Foundatio.AzureStorage.Queues;
using Microsoft.WindowsAzure.Storage.RetryPolicies;
using Xunit.Abstractions;

namespace Foundatio.Azure.Tests.Queue {
    public class AzureStorageQueueTests : QueueTestBase {
        private readonly static string QueueName = Guid.NewGuid().ToString("N");

        public AzureStorageQueueTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

        protected override IQueue<SimpleWorkItem> GetQueue(int retries = 1, TimeSpan? workItemTimeout = null, TimeSpan? retryDelay = null, int deadLetterMaxItems = 100, bool runQueueMaintenance = true) {
            string connectionString = ConnectionStrings.Get("AzureStorageConnectionString");

            if (connectionString == null)
                return null;

            if (connectionString == "UseDevelopmentStorage=true;" && !Process.GetProcessesByName("AzureStorageEmulator").Any()) {
                var x64 = Directory.Exists(@"C:\Program Files (x86)");
                var process = Process.Start($@"C:\Program Files{(x64 ? " (x86)" : "")}\Microsoft SDKs\Azure\Storage Emulator\AzureStorageEmulator.exe", "start");

                if (process != null) {
                    process.WaitForExit();
                }
                else {
                    throw new Exception("Unable to start storage emulator.");
                }
            }

            if (!retryDelay.HasValue)
                retryDelay = TimeSpan.FromSeconds(1);
            
            return new AzureStorageQueue<SimpleWorkItem>(
                connectionString,
                QueueName,
                retries,
                workItemTimeout,
                TimeSpan.FromMilliseconds(50),
                new ExponentialRetry(retryDelay.Value, retries + 1)
            );
        }
        
        [Fact]
        public override Task CanQueueAndDequeueWorkItem() {
            return base.CanQueueAndDequeueWorkItem();
        }

        [Fact]
        public override Task CanDequeueWithCancelledToken() {
            return base.CanDequeueWithCancelledToken();
        }

        [Fact]
        public override Task CanQueueAndDequeueMultipleWorkItems() {
            return base.CanQueueAndDequeueMultipleWorkItems();
        }

        [Fact]
        public override Task WillWaitForItem() {
            return base.WillWaitForItem();
        }

        [Fact]
        public override Task DequeueWaitWillGetSignaled() {
            return base.DequeueWaitWillGetSignaled();
        }

        [Fact]
        public override Task CanUseQueueWorker() {
            return base.CanUseQueueWorker();
        }

        [Fact]
        public override Task CanHandleErrorInWorker() {
            return base.CanHandleErrorInWorker();
        }

        [Fact]
        public override Task WorkItemsWillTimeout() {
            return base.WorkItemsWillTimeout();
        }

        [Fact]
        public override Task WorkItemsWillGetMovedToDeadletter() {
            return base.WorkItemsWillGetMovedToDeadletter();
        }

        [Fact]
        public override Task CanAutoCompleteWorker() {
            return base.CanAutoCompleteWorker();
        }

        [Fact]
        public override Task CanHaveMultipleQueueInstances() {
            return base.CanHaveMultipleQueueInstances();
        }

        [Fact]
        public override Task CanRunWorkItemWithMetrics() {
            return base.CanRunWorkItemWithMetrics();
        }

        // NOTE: Not using this test because you can set specific delay times for storage queue
        public override Task CanDelayRetry() {
            return base.CanDelayRetry();
        }
    }
}