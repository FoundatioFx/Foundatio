using System;
using Foundatio.Storage;
using Xunit;

namespace Foundatio.Tests.Storage {
    public class InMemoryFileStorage2Tests : FileStorage2TestsBase {
        protected override IFileStorage2 GetStorage() {
            return new InMemoryFileStorage2();
        }

        [Fact]
        public override void CanGetEmptyFileListOnMissingDirectory() {
            base.CanGetEmptyFileListOnMissingDirectory();
        }
        
        [Fact]
        public override void CanGetFileListForSingleFolder() {
            base.CanGetFileListForSingleFolder();
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
