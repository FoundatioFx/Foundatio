using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Foundatio.Storage;
using Foundatio.Tests.Storage;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.AWS.Tests.Storage {
    public class ScopedS3StorageTests : FileStorageTestsBase {
        public ScopedS3StorageTests(ITestOutputHelper output) : base(output) {}

        protected override IFileStorage GetStorage() {
            return new S3FileStorage(
                o => o.ConnectionString($"serviceurl=http://localhost:4566;bucket=foundatio-ci;AccessKey=xxx;SecretKey=xxx")
                    .LoggerFactory(Log));
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
        public override Task CanGetPagedFileListForSingleFolderAsync() {
            return base.CanGetPagedFileListForSingleFolderAsync();
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
        public override Task CanDeleteEntireFolderAsync() {
            return base.CanDeleteEntireFolderAsync();
        }

        [Fact]
        public override Task CanDeleteEntireFolderWithWildcardAsync() {
            return base.CanDeleteEntireFolderWithWildcardAsync();
        }

        [Fact]
        public override Task CanDeleteFolderWithMultiFolderWildcardsAsync() {
            return base.CanDeleteFolderWithMultiFolderWildcardsAsync();
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

        protected override async Task ResetAsync(IFileStorage storage) {
            var client = new AmazonS3Client(
                new BasicAWSCredentials("xxx", "xxx"),
                new AmazonS3Config {
                    RegionEndpoint = RegionEndpoint.USEast1,
                    ServiceURL = "http://localhost:4566",
                    ForcePathStyle = true
                });
            await client.PutBucketAsync("foundatio-ci");

            await base.ResetAsync(storage);
        }
    }
}
