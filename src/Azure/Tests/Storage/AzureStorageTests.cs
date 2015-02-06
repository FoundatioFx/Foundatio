using System;
using Foundatio;
using Foundatio.Storage;
using Foundatio.Tests.Storage;

namespace Foundatio.Azure.Tests.Storage {
    public class AzureStorageTests : FileStorageTestsBase {
        protected override IFileStorage GetStorage() {
            return null; //new AzureFileStorage(Settings.Current.AzureStorageConnectionString);
        }
    }
}
