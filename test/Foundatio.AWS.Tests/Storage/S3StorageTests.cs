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
    public class S3StorageTests : FileStorageTestsBase {
        public S3StorageTests(ITestOutputHelper output) : base(output) {}

        protected override IFileStorage GetStorage() {
            var section = Configuration.GetSection("AWS");
            string accessKey = section["ACCESS_KEY_ID"];
            string secretKey = section["SECRET_ACCESS_KEY"];
            if (String.IsNullOrEmpty(accessKey) || String.IsNullOrEmpty(secretKey))
                return null;

            return new S3Storage(new BasicAWSCredentials(accessKey, secretKey), RegionEndpoint.USEast1, "foundatio", loggerFactory: Log);
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
