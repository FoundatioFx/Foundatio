using System;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Foundatio.Storage;
using Foundatio.Tests.Storage;
using Xunit;
using Xunit.Abstractions;
using Foundatio.Tests.Utility;

namespace Foundatio.AWS.Tests.Storage {
    public class S3FileStorageTests : FileStorageTestsBase {
        public S3FileStorageTests(ITestOutputHelper output) : base(output) {}

        protected override IFileStorage GetStorage() {
            var section = Configuration.GetSection("AWS");
            string accessKey = section["ACCESS_KEY_ID"];
            string secretKey = section["SECRET_ACCESS_KEY"];
            if (String.IsNullOrEmpty(accessKey) || String.IsNullOrEmpty(secretKey))
                return null;

            return new S3FileStorage(new BasicAWSCredentials(accessKey, secretKey), RegionEndpoint.USEast1, "foundatio", loggerFactory: Log);
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
        public override Task CanConcurrentlyManageFilesAsync() {
            return base.CanConcurrentlyManageFilesAsync();
        }
    }
}
