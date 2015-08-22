using System.Threading.Tasks;
using Foundatio.Storage;
using Foundatio.Tests.Storage;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Azure.Tests.Storage {
    public class AzureStorageTests : FileStorageTestsBase {
        public AzureStorageTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output)
        {
        }

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
        public override Task CanSaveFilesAsync() {
            return base.CanSaveFilesAsync();
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
