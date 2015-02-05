using System;
using Foundatio.Storage;

namespace Foundatio.Tests.Storage {
    public class InMemoryFileStorageTests : FileStorageTestsBase {
        protected override IFileStorage GetStorage() {
            return new InMemoryFileStorage();
        }
    }
}
