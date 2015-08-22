using System.Threading.Tasks;
using Foundatio.Storage;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Storage {
    public class InMemoryFileStorageTests : FileStorageTestsBase {
        public InMemoryFileStorageTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output)
        {
        }

        protected override IFileStorage GetStorage() {
            return new InMemoryFileStorage();
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
        public override Task CanSaveFilesAsync() {
            return base.CanSaveFilesAsync();
        }

        [Fact]
        public override void CanConcurrentlyManageFiles() {
            base.CanConcurrentlyManageFiles();
        }
    }
}
