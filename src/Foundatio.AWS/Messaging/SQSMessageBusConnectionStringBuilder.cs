namespace Foundatio.Messaging;

public class SQSMessageBusConnectionStringBuilder : AmazonConnectionStringBuilder
{
    public SQSMessageBusConnectionStringBuilder()
    {
    }

    public SQSMessageBusConnectionStringBuilder(string connectionString) : base(connectionString)
    {
    }
}
