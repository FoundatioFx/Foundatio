using System.Threading.Tasks;
using Foundatio.Storage;
using Foundatio.Tests.Storage;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Azure.Tests.Storage {
    public class AzureStorageTests : FileStorageTestsBase {
        public AzureStorageTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output) {}

        protected override IFileStorage GetStorage() {
            if (ConnectionStrings.Get("AzureStorageConnectionString") == null)
                return null;

            return new AzureFileStorage(ConnectionStrings.Get("AzureStorageConnectionString"));
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
