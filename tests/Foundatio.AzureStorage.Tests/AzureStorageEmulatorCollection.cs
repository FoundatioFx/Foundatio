using Foundatio.Azure.Tests;
using Xunit;

namespace Foundato.Azure.Tests {
#if DEBUG
    [CollectionDefinition("AzureStorageIntegrationTests")]
    public class AzureStorageEmulatorCollection : ICollectionFixture<AzureStorageEmulatorFixture> { }
#endif
}