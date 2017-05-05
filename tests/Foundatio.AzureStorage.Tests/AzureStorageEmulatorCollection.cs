using Foundatio.Azure.Tests;
using Xunit;

namespace Foundato.Azure.Tests {
    [CollectionDefinition("AzureStorageIntegrationTests")]
    public class AzureStorageEmulatorCollection : ICollectionFixture<AzureStorageEmulatorFixture> { }
}