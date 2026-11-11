using Foundatio.Messaging;
using Xunit;

namespace Foundatio.AWS.Tests.Messaging;

public class SQSMessageBusConnectionStringBuilderTests : ConnectionStringBuilderTests
{
    protected override AmazonConnectionStringBuilder CreateConnectionStringBuilder(string connectionString)
    {
        return new SQSMessageBusConnectionStringBuilder(connectionString);
    }

    protected override AmazonConnectionStringBuilder CreateConnectionStringBuilder()
    {
        return new SQSMessageBusConnectionStringBuilder();
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
