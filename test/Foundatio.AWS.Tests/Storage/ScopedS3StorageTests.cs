using System;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Foundatio.Storage;
using Foundatio.Tests.Utility;
using Xunit;
using Xunit.Abstractions;

namespace Foundatio.Tests.Storage {
    public class ScopedS3StorageTests : FileStorageTestsBase {
        public ScopedS3StorageTests(ITestOutputHelper output) : base(output) {}

        protected override IFileStorage GetStorage() {
            var section = Configuration.GetSection("AWS");
            string accessKey = section["ACCESS_KEY_ID"];
            string secretKey = section["SECRET_ACCESS_KEY"];
            if (String.IsNullOrEmpty(accessKey) || String.IsNullOrEmpty(secretKey))
                return null;

            return new ScopedFileStorage(new S3Storage(new BasicAWSCredentials(accessKey, secretKey), RegionEndpoint.USEast1, "foundatio", loggerFactory: Log), "scope");
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
