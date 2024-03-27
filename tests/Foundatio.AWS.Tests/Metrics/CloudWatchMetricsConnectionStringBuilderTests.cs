using Amazon.CloudWatch.Model;
using Foundatio.Metrics;
using Xunit;

namespace Foundatio.AWS.Tests.Metrics;

public class CloudWatchMetricsConnectionStringBuilderTests : ConnectionStringBuilderTests
{
    protected override AmazonConnectionStringBuilder CreateConnectionStringBuilder(string connectionString)
    {
        return new CloudWatchMetricsConnectionStringBuilder(connectionString);
    }

    protected override AmazonConnectionStringBuilder CreateConnectionStringBuilder()
    {
        return new CloudWatchMetricsConnectionStringBuilder();
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
    public void CanParseNamespace()
    {
        foreach (var key in new[] { "Namespace", "namespace" })
        {
            var connectionStringBuilder = CreateConnectionStringBuilder($"AccessKey=TestAccessKey;SecretKey=TestSecretKey;{key}=TestNamespace");
            Assert.Equal("TestAccessKey", connectionStringBuilder.AccessKey);
            Assert.Equal("TestSecretKey", connectionStringBuilder.SecretKey);
            Assert.Null(connectionStringBuilder.Region);
            var cloudWatchMetricsConnectionStringBuilder = Assert.IsType<CloudWatchMetricsConnectionStringBuilder>(connectionStringBuilder);
            Assert.Equal("TestNamespace", cloudWatchMetricsConnectionStringBuilder.Namespace);
        }
    }

    [Fact]
    public void UnknownKeyWillBeDimensions()
    {
        var connectionStringBuilder = CreateConnectionStringBuilder("AccessKey=TestAccessKey;SecretKey=TestSecretKey;Namespace=TestNamespace;key1=value1;key2=value2");
        Assert.Equal("TestAccessKey", connectionStringBuilder.AccessKey);
        Assert.Equal("TestSecretKey", connectionStringBuilder.SecretKey);
        var cloudWatchMetricsConnectionStringBuilder = Assert.IsType<CloudWatchMetricsConnectionStringBuilder>(connectionStringBuilder);
        Assert.Equal("TestNamespace", cloudWatchMetricsConnectionStringBuilder.Namespace);
        Assert.Equal(2, cloudWatchMetricsConnectionStringBuilder.Dimensions.Count);
        Assert.Equal("key1", cloudWatchMetricsConnectionStringBuilder.Dimensions[0].Name);
        Assert.Equal("key2", cloudWatchMetricsConnectionStringBuilder.Dimensions[1].Name);
        Assert.Equal("value1", cloudWatchMetricsConnectionStringBuilder.Dimensions[0].Value);
        Assert.Equal("value2", cloudWatchMetricsConnectionStringBuilder.Dimensions[1].Value);
    }

    [Fact]
    public void CanGenerateConnectionStringWithNamespace()
    {
        var connectionStringBuilder = (CloudWatchMetricsConnectionStringBuilder)CreateConnectionStringBuilder();
        connectionStringBuilder.AccessKey = "TestAccessKey";
        connectionStringBuilder.SecretKey = "TestSecretKey";
        connectionStringBuilder.Region = "TestRegion";
        connectionStringBuilder.Namespace = "TestNamespace";

        Assert.Equal("AccessKey=TestAccessKey;SecretKey=TestSecretKey;Region=TestRegion;Namespace=TestNamespace;", connectionStringBuilder.ToString());
    }

    [Fact]
    public void CanGenerateConnectionStringWithDimensions()
    {
        var connectionStringBuilder = (CloudWatchMetricsConnectionStringBuilder)CreateConnectionStringBuilder();
        connectionStringBuilder.AccessKey = "TestAccessKey";
        connectionStringBuilder.SecretKey = "TestSecretKey";
        connectionStringBuilder.Region = "TestRegion";
        connectionStringBuilder.Dimensions.Add(new Dimension { Name = "key1", Value = "value1" });
        connectionStringBuilder.Dimensions.Add(new Dimension { Name = "key2", Value = "value2" });

        Assert.Equal("AccessKey=TestAccessKey;SecretKey=TestSecretKey;Region=TestRegion;key1=value1;key2=value2;", connectionStringBuilder.ToString());
    }

    [Fact]
    public void CanGenerateConnectionStringWithAll()
    {
        var connectionStringBuilder = (CloudWatchMetricsConnectionStringBuilder)CreateConnectionStringBuilder();
        connectionStringBuilder.AccessKey = "TestAccessKey";
        connectionStringBuilder.SecretKey = "TestSecretKey";
        connectionStringBuilder.Region = "TestRegion";
        connectionStringBuilder.Namespace = "TestNamespace";
        connectionStringBuilder.Dimensions.Add(new Dimension { Name = "key1", Value = "value1" });
        connectionStringBuilder.Dimensions.Add(new Dimension { Name = "key2", Value = "value2" });

        Assert.Equal("AccessKey=TestAccessKey;SecretKey=TestSecretKey;Region=TestRegion;Namespace=TestNamespace;key1=value1;key2=value2;", connectionStringBuilder.ToString());
    }
}
