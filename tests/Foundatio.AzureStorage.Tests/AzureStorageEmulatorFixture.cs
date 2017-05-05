using System;
using RimDev.Automation.StorageEmulator;

namespace Foundatio.Azure.Tests {
    public class AzureStorageEmulatorFixture : IDisposable {
        private readonly AzureStorageEmulatorAutomation _automation;

        public AzureStorageEmulatorFixture() {
            _automation = new AzureStorageEmulatorAutomation();
            _automation.Start();
        }

        public void Dispose() {
            _automation.Dispose();
        }
    }
}