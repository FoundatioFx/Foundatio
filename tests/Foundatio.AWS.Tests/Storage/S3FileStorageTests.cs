using System;
using System.Threading.Tasks;
using Amazon.S3.Model;
using Foundatio.Storage;
using Foundatio.Tests.Storage;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.AWS.Tests.Storage
{
    public class S3FileStorageTests : FileStorageTestsBase
    {
        private const string BUCKET_NAME = "foundatio-ci";

        public S3FileStorageTests(ITestOutputHelper output) : base(output) { }

        protected override IFileStorage GetStorage()
        {
            return new S3FileStorage(
                o => o.ConnectionString($"serviceurl=http://localhost:4566;bucket={BUCKET_NAME};AccessKey=xxx;SecretKey=xxx")
                    .LoggerFactory(Log));
        }

        [Fact]
        public override Task CanGetEmptyFileListOnMissingDirectoryAsync()
        {
            return base.CanGetEmptyFileListOnMissingDirectoryAsync();
        }

        [Fact]
        public override Task CanGetFileListForSingleFolderAsync()
        {
            return base.CanGetFileListForSingleFolderAsync();
        }

        [Fact]
        public override Task CanGetPagedFileListForSingleFolderAsync()
        {
            return base.CanGetPagedFileListForSingleFolderAsync();
        }

        [Fact]
        public override Task CanGetFileInfoAsync()
        {
            return base.CanGetFileInfoAsync();
        }

        [Fact]
        public override Task CanGetNonExistentFileInfoAsync()
        {
            return base.CanGetNonExistentFileInfoAsync();
        }

        [Fact]
        public override Task CanSaveFilesAsync()
        {
            return base.CanSaveFilesAsync();
        }

        [Fact]
        public override Task CanManageFilesAsync()
        {
            return base.CanManageFilesAsync();
        }

        [Fact]
        public override Task CanRenameFilesAsync()
        {
            return base.CanRenameFilesAsync();
        }

        [Fact]
        public override Task CanConcurrentlyManageFilesAsync()
        {
            return base.CanConcurrentlyManageFilesAsync();
        }

        [Fact]
        public override void CanUseDataDirectory()
        {
            base.CanUseDataDirectory();
        }

        [Fact]
        public override Task CanDeleteEntireFolderAsync()
        {
            return base.CanDeleteEntireFolderAsync();
        }

        [Fact]
        public override Task CanDeleteEntireFolderWithWildcardAsync()
        {
            return base.CanDeleteEntireFolderWithWildcardAsync();
        }

        [Fact]
        public override Task CanDeleteSpecificFilesAsync()
        {
            return base.CanDeleteSpecificFilesAsync();
        }

        [Fact]
        public override Task CanDeleteNestedFolderAsync()
        {
            return base.CanDeleteNestedFolderAsync();
        }

        [Fact]
        public override Task CanDeleteSpecificFilesInNestedFolderAsync()
        {
            return base.CanDeleteSpecificFilesInNestedFolderAsync();
        }

        [Fact]
        public override Task CanDeleteFolderWithMultiFolderWildcardsAsync()
        {
            return base.CanDeleteFolderWithMultiFolderWildcardsAsync();
        }

        [Fact]
        public override Task CanRoundTripSeekableStreamAsync()
        {
            return base.CanRoundTripSeekableStreamAsync();
        }

        [Fact]
        public override Task WillRespectStreamOffsetAsync()
        {
            return base.WillRespectStreamOffsetAsync();
        }

        [Fact]
        public virtual async Task WillNotReturnDirectoryInGetPagedFileListAsync()
        {
            var storage = GetStorage();
            if (storage == null)
                return;

            await ResetAsync(storage);

            using (storage)
            {
                var result = await storage.GetPagedFileListAsync();
                Assert.False(result.HasMore);
                Assert.Empty(result.Files);
                Assert.False(await result.NextPageAsync());
                Assert.False(result.HasMore);
                Assert.Empty(result.Files);

                // To create an empty folder (or what appears as a folder) in an Amazon S3 bucket using the AWS SDK for .NET,
                // you typically create an object with a key that ends with a trailing slash ('/') because S3 doesn't
                // actually have a concept of folders, but it mimics the behavior of folders using object keys.
                var client = storage is S3FileStorage s3Storage ? s3Storage.Client : null;
                Assert.NotNull(client);

                const string folderName = "EmptyFolder/";
                await client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = BUCKET_NAME,
                    Key = folderName,
                    ContentBody = String.Empty
                });

                result = await storage.GetPagedFileListAsync();
                Assert.False(result.HasMore);
                Assert.Empty(result.Files);
                Assert.False(await result.NextPageAsync());
                Assert.False(result.HasMore);
                Assert.Empty(result.Files);

                // Ensure the file can be returned via get file info
                var info = await storage.GetFileInfoAsync(folderName);
                Assert.NotNull(info?.Path);

                // Ensure delete files can remove all files including fake folders
                await storage.DeleteFilesAsync("*");

                info = await storage.GetFileInfoAsync(folderName);
                Assert.Null(info);
            }
        }

        protected override async Task ResetAsync(IFileStorage storage)
        {
            var client = storage is S3FileStorage s3Storage ? s3Storage.Client : null;
            Assert.NotNull(client);
            await client.PutBucketAsync(BUCKET_NAME);

            await base.ResetAsync(storage);
        }
    }
}
