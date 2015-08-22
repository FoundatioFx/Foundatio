using System.Threading.Tasks;
using Foundatio.Storage;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Storage {
    public class FolderFileStorageTests : FileStorageTestsBase {
        private const string DATA_DIRECTORY_QUEUE_FOLDER = @"|DataDirectory|\Queue";

        public FolderFileStorageTests(CaptureFixture fixture, ITestOutputHelper output) : base(fixture, output)
        {
        }

        protected override IFileStorage GetStorage() {
            return new FolderFileStorage("temp");
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

        [Fact]
        public void CanUseDataDirectory() {
            var storage = new FolderFileStorage(DATA_DIRECTORY_QUEUE_FOLDER);
            Assert.NotNull(storage.Folder);
            Assert.NotEqual(DATA_DIRECTORY_QUEUE_FOLDER, storage.Folder);
            Assert.True(storage.Folder.EndsWith("Queue\\"), storage.Folder);
        }
    }
}
