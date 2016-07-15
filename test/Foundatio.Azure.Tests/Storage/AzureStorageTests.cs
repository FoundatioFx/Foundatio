using System;
using System.Threading.Tasks;
using Foundatio.Storage;
using Foundatio.Tests.Storage;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Azure.Tests.Storage {
    [Collection("AzureStorageIntegrationTests")]
    public class AzureStorageTests : FileStorageTestsBase {
        public AzureStorageTests(ITestOutputHelper output) : base(output) {}

        protected override IFileStorage GetStorage() {
            if (String.IsNullOrEmpty(Configuration.GetConnectionString("AzureStorageConnectionString")))
                return null;

            return new AzureFileStorage(Configuration.GetConnectionString("AzureStorageConnectionString"));
        }

        [Fact]
        public override Task CanGetEmptyFileListOnMissingDirectory() {
            return base.CanGetEmptyFileListOnMissingDirectory();
        }
        
        [Fact]
        public override Task CanGetFileListForSingleFolder() {
            return base.CanGetFileListForSingleFolder();
        }

        [Fact]
        public override Task CanSaveFilesAsync() {
            return base.CanSaveFilesAsync();
        }

        [Fact]
        public override Task CanManageFiles() {
            return base.CanManageFiles();
        }

        [Fact]
        public override Task CanConcurrentlyManageFiles() {
            return base.CanConcurrentlyManageFiles();
        }
    }
}
