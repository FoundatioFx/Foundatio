using Amazon.Runtime;
using Foundatio.Messaging.Benchmarks;
using Xunit;

namespace Foundatio.Messaging.Benchmarks.Tests;

[CollectionDefinition("AWS benchmark environment", DisableParallelization = true)]
public class AwsEnvironmentCollection;

[Collection("AWS benchmark environment")]
public class AwsResourcesTests : IDisposable
{
    private readonly Dictionary<string, string?> _original = new[] { "PERF_AWS_MODE", "PERF_AWS_URL", "PERF_AWS_REGION" }
        .ToDictionary(name => name, Environment.GetEnvironmentVariable);

    public AwsResourcesTests()
    {
        foreach (string name in _original.Keys)
            Environment.SetEnvironmentVariable(name, null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("localstack")]
    [InlineData("LOCALSTACK")]
    public void Configuration_DefaultOrLocalStack_UsesOnlyEmulatorCredentials(string? mode)
    {
        Environment.SetEnvironmentVariable("PERF_AWS_MODE", mode);

        Assert.Equal("http://localhost:24566", AwsResources.ServiceUrl);
        Assert.Equal("us-east-1", AwsResources.Region.SystemName);
        AssertLocalClient(AwsResources.SqsConfig, "http://localhost:24566", "us-east-1");
        AssertLocalClient(AwsResources.SnsConfig, "http://localhost:24566", "us-east-1");
        var credentials = Assert.IsType<BasicAWSCredentials>(AwsResources.LocalCredentials).GetCredentials();
        Assert.Equal("test", credentials.AccessKey);
        Assert.Equal("test", credentials.SecretKey);
    }

    [Fact]
    public void Configuration_LocalOverrides_AppliesEndpointAndSigningRegionToBothServices()
    {
        Environment.SetEnvironmentVariable("PERF_AWS_URL", "http://localhost:34566");
        Environment.SetEnvironmentVariable("PERF_AWS_REGION", "eu-west-1");

        AssertLocalClient(AwsResources.SqsConfig, "http://localhost:34566", "eu-west-1");
        AssertLocalClient(AwsResources.SnsConfig, "http://localhost:34566", "eu-west-1");
        Assert.NotNull(AwsResources.LocalCredentials);
    }

    [Theory]
    [InlineData("live")]
    [InlineData("LIVE")]
    [InlineData(" live ")]
    public void Configuration_Live_IgnoresEmulatorEndpointAndLeavesCredentialsToSdk(string mode)
    {
        Environment.SetEnvironmentVariable("PERF_AWS_MODE", mode);
        Environment.SetEnvironmentVariable("PERF_AWS_URL", "http://localhost:34566");
        Environment.SetEnvironmentVariable("PERF_AWS_REGION", "eu-west-1");

        Assert.Null(AwsResources.ServiceUrl);
        Assert.Null(AwsResources.LocalCredentials);
        Assert.Null(AwsResources.SqsConfig.ServiceURL);
        Assert.Null(AwsResources.SnsConfig.ServiceURL);
        Assert.Equal("eu-west-1", AwsResources.SqsConfig.RegionEndpoint.SystemName);
        Assert.Equal("eu-west-1", AwsResources.SnsConfig.RegionEndpoint.SystemName);
    }

    [Theory]
    [InlineData("aws")]
    [InlineData("liev")]
    public void Configuration_UnknownMode_FailsBeforeConnecting(string mode)
    {
        Environment.SetEnvironmentVariable("PERF_AWS_MODE", mode);

        var error = Assert.Throws<ArgumentException>(() => AwsResources.ServiceUrl);
        Assert.Contains("PERF_AWS_MODE", error.Message);
        Assert.Contains("localstack", error.Message);
        Assert.Contains("live", error.Message);
    }

    private static void AssertLocalClient(ClientConfig config, string endpoint, string region)
    {
        Assert.Equal(new Uri(endpoint), new Uri(config.ServiceURL));
        Assert.Equal(region, config.AuthenticationRegion);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _original)
            Environment.SetEnvironmentVariable(name, value);
    }
}
