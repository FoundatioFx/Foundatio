using Foundatio.Storage;
using Xunit;

namespace Foundatio.AWS.Tests.Storage
{
    public class S3FileStorageConnectionStringBuilderTests : ConnectionStringBuilderTests
    {
        protected override AmazonConnectionStringBuilder CreateConnectionStringBuilder(string connectionString)
        {
            return new S3FileStorageConnectionStringBuilder(connectionString);
        }

        protected override AmazonConnectionStringBuilder CreateConnectionStringBuilder()
        {
            return new S3FileStorageConnectionStringBuilder();
        }

        [Fact]
        public override void InvalidKeyShouldThrow()
        {
            base.InvalidKeyShouldThrow();
        }

        [Fact]
        public override void CanParseAccessKey()
        {
            base.CanParseAccessKey();
        }

        [Fact]
        public override void CanParseSecretKey()
        {
            base.CanParseSecretKey();
        }

        [Fact]
        public override void CanParseRegion()
        {
            base.CanParseRegion();
        }

        [Fact]
        public override void CanGenerateConnectionString()
        {
            base.CanGenerateConnectionString();
        }

        [Fact]
        public void CanParseBucket()
        {
            foreach (var key in new[] { "Bucket", "bucket" })
            {
                var connectionStringBuilder = CreateConnectionStringBuilder($"AccessKey=TestAccessKey;SecretKey=TestSecretKey;{key}=TestBucket");
                Assert.Equal("TestAccessKey", connectionStringBuilder.AccessKey);
                Assert.Equal("TestSecretKey", connectionStringBuilder.SecretKey);
                Assert.Equal("TestBucket", ((S3FileStorageConnectionStringBuilder)connectionStringBuilder).Bucket);
                Assert.Null(connectionStringBuilder.Region);
            }
        }

        [Fact]
        public void CanGenerateConnectionStringWithBucket()
        {
            var connectionStringBuilder = (S3FileStorageConnectionStringBuilder)CreateConnectionStringBuilder();
            connectionStringBuilder.AccessKey = "TestAccessKey";
            connectionStringBuilder.SecretKey = "TestSecretKey";
            connectionStringBuilder.Region = "TestRegion";
            connectionStringBuilder.Bucket = "TestBucket";

            Assert.Equal("AccessKey=TestAccessKey;SecretKey=TestSecretKey;Region=TestRegion;Bucket=TestBucket;", connectionStringBuilder.ToString());
        }
    }
}
