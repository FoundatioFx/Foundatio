using System;
using Foundatio;
using Foundatio.Storage;

namespace Foundatio.Tests.Storage {
    public class AzureStorageTests : FileStorageTestsBase {
        protected override IFileStorage GetStorage() {
            return null; //new AzureFileStorage(Settings.Current.AzureStorageConnectionString);
        }
    }
}
