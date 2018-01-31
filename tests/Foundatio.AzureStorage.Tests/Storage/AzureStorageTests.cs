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
            string connectionString = Configuration.GetConnectionString("AzureStorageConnectionString");
            if (String.IsNullOrEmpty(connectionString))
                return null;

            return new AzureFileStorage(o => o.ConnectionString(connectionString).LoggerFactory(Log));
        }

        [Fact]
        public override Task CanGetEmptyFileListOnMissingDirectoryAsync() {
            return base.CanGetEmptyFileListOnMissingDirectoryAsync();
        }

        [Fact]
        public override Task CanGetFileListForSingleFolderAsync() {
            return base.CanGetFileListForSingleFolderAsync();
        }

        [Fact]
        public override Task CanGetFileInfoAsync() {
            return base.CanGetFileInfoAsync();
        }

        [Fact]
        public override Task CanGetNonExistentFileInfoAsync() {
            return base.CanGetNonExistentFileInfoAsync();
        }

        [Fact]
        public override Task CanSaveFilesAsync() {
            return base.CanSaveFilesAsync();
        }

        [Fact]
        public override Task CanManageFilesAsync() {
            return base.CanManageFilesAsync();
        }

        [Fact]
        public override Task CanRenameFilesAsync() {
            return base.CanRenameFilesAsync();
        }

        [Fact]
        public override Task CanConcurrentlyManageFilesAsync() {
            return base.CanConcurrentlyManageFilesAsync();
        }

        [Fact]
        public override void CanUseDataDirectory() {
            base.CanUseDataDirectory();
        }

        [Fact]
        public override Task CanDeleteEntireFolderAsync() {
            return base.CanDeleteEntireFolderAsync();
        }

        [Fact]
        public override Task CanDeleteEntireFolderWithWildcardAsync() {
            return base.CanDeleteEntireFolderWithWildcardAsync();
        }

        [Fact]
        public override Task CanDeleteSpecificFilesAsync() {
            return base.CanDeleteSpecificFilesAsync();
        }

        [Fact]
        public override Task CanDeleteNestedFolderAsync() {
            return base.CanDeleteNestedFolderAsync();
        }

        [Fact]
        public override Task CanDeleteSpecificFilesInNestedFolderAsync() {
            return base.CanDeleteSpecificFilesInNestedFolderAsync();
        }

        [Fact]
        public override Task CanRoundTripSeekableStreamAsync() {
            return base.CanRoundTripSeekableStreamAsync();
        }

        [Fact]
        public override Task WillRespectStreamOffsetAsync() {
            return base.WillRespectStreamOffsetAsync();
        }
    }
}