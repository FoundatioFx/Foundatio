using Foundatio.Queues;
using Xunit;

namespace Foundatio.AWS.Tests.Queues;

public class SQSQueueConnectionStringBuilderTests : ConnectionStringBuilderTests
{
    protected override AmazonConnectionStringBuilder CreateConnectionStringBuilder(string connectionString)
    {
        return new SQSQueueConnectionStringBuilder(connectionString);
    }

    protected override AmazonConnectionStringBuilder CreateConnectionStringBuilder()
    {
        return new SQSQueueConnectionStringBuilder();
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
}
