namespace Foundatio.Messaging.Transport;

internal class SqsMessageTransportConnectionStringBuilder : AmazonConnectionStringBuilder
{
    public SqsMessageTransportConnectionStringBuilder()
    {
    }

    public SqsMessageTransportConnectionStringBuilder(string connectionString) : base(connectionString)
    {
    }
}
