using System;
using Xunit;

namespace Foundatio.AWS.Tests {
    public abstract class ConnectionStringBuilderTests {
        protected abstract AmazonConnectionStringBuilder CreateConnectionStringBuilder(string connectionString);

        protected abstract AmazonConnectionStringBuilder CreateConnectionStringBuilder();

        public virtual void InvalidKeyShouldThrow() {
            var exception = Assert.Throws<ArgumentException>("connectionString",() => CreateConnectionStringBuilder("wrongaccess=TestAccessKey;SecretKey=TestSecretKey"));
            Assert.Equal("The option 'wrongaccess' cannot be recognized in connection string. (Parameter 'connectionString')", exception.Message);
        }

        public virtual void CanParseAccessKey() {
            foreach (string key in new[] { "AccessKey", "AccessKeyId", "Access Key", "Access Key ID", "Id", "accessKey", "access key", "access key id", "id" }) {
                var connectionStringBuilder = CreateConnectionStringBuilder($"{key}=TestAccessKey;SecretKey=TestSecretKey;");
                Assert.Equal("TestAccessKey", connectionStringBuilder.AccessKey);
                Assert.Equal("TestSecretKey", connectionStringBuilder.SecretKey);
                Assert.Null(connectionStringBuilder.Region);
            }
        }

        public virtual void CanParseSecretKey() {
            foreach (string key in new[] { "SecretKey", "Secret Key", "Secret", "secretKey", "secret key", "secret" }) {
                var connectionStringBuilder = CreateConnectionStringBuilder($"AccessKey=TestAccessKey;{key}=TestSecretKey;");
                Assert.Equal("TestAccessKey", connectionStringBuilder.AccessKey);
                Assert.Equal("TestSecretKey", connectionStringBuilder.SecretKey);
                Assert.Null(connectionStringBuilder.Region);
            }
        }

        public virtual void CanParseRegion() {
            foreach (string key in new[] { "EndPoint", "End Point", "Region", "endPoint", "end point", "region" }) {
                var connectionStringBuilder = CreateConnectionStringBuilder($"AccessKey=TestAccessKey;SecretKey=TestSecretKey;{key}=TestRegion;");
                Assert.Equal("TestAccessKey", connectionStringBuilder.AccessKey);
                Assert.Equal("TestSecretKey", connectionStringBuilder.SecretKey);
                Assert.Equal("TestRegion", connectionStringBuilder.Region);
            }
        }

        public virtual void CanGenerateConnectionString() {
            var connectionStringBuilder = CreateConnectionStringBuilder();
            connectionStringBuilder.AccessKey = "TestAccessKey";
            connectionStringBuilder.SecretKey = "TestSecretKey";
            connectionStringBuilder.Region = "TestRegion";

            Assert.Equal("AccessKey=TestAccessKey;SecretKey=TestSecretKey;Region=TestRegion;", connectionStringBuilder.ToString());
        }
    }
}
