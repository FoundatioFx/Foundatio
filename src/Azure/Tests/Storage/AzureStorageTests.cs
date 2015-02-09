using System;
using Foundatio;
using Foundatio.Storage;
using Foundatio.Tests.Storage;
using Foundatio.Tests.Utility;

namespace Foundatio.Azure.Tests.Storage {
    public class AzureStorageTests : FileStorageTestsBase {
        protected override IFileStorage GetStorage() {
            if (ConnectionStrings.Get("AzureStorageConnectionString") == null)
                return null;

            return new AzureFileStorage(ConnectionStrings.Get("AzureStorageConnectionString"));
        }
    }
}
