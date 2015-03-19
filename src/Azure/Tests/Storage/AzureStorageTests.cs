using System;
using Foundatio.Storage;
using Foundatio.Tests.Storage;
using Foundatio.Tests.Utility;
using Xunit;

namespace Foundatio.Azure.Tests.Storage {
    public class AzureStorageTests : FileStorageTestsBase {
        protected override IFileStorage GetStorage() {
            if (ConnectionStrings.Get("AzureStorageConnectionString") == null)
                return null;

            return new AzureFileStorage(ConnectionStrings.Get("AzureStorageConnectionString"));
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
