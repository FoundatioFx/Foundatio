namespace Foundatio.Messaging.Transport;

public class SqsMessageTransportConnectionStringBuilder : AmazonConnectionStringBuilder
{
    public SqsMessageTransportConnectionStringBuilder()
    {
    }

    public SqsMessageTransportConnectionStringBuilder(string connectionString) : base(connectionString)
    {
    }
}
