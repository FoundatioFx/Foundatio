using System;
using Foundatio.Storage;
using Xunit;

namespace Foundatio.Tests.Storage {
    public class InMemoryFileStorageTests : FileStorageTestsBase {
        protected override IFileStorage GetStorage() {
            return new InMemoryFileStorage();
        }

        [Fact]
        public override void CanManageFiles() {
            base.CanManageFiles();
        }

        [Fact]
        public override void CanConcurrentlyManageFiles() {
            base.CanConcurrentlyManageFiles();
        }
    }
}
