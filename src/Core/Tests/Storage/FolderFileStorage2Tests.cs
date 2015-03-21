using Foundatio.Storage;
using Xunit;

namespace Foundatio.Tests.Storage {
    public class FolderFileStorage2Tests : FileStorage2TestsBase {
        private const string DATA_DIRECTORY_QUEUE_FOLDER = @"|DataDirectory|\Queue";

        protected override IFileStorage2 GetStorage() {
            return new FolderFileStorage2("temp");
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

        [Fact]
        public void CanUseDataDirectory() {
            var storage = new FolderFileStorage(DATA_DIRECTORY_QUEUE_FOLDER);
            Assert.NotNull(storage.Folder);
            Assert.NotEqual(DATA_DIRECTORY_QUEUE_FOLDER, storage.Folder);
            Assert.True(storage.Folder.EndsWith("Queue\\"), storage.Folder);
        }
    }
}
