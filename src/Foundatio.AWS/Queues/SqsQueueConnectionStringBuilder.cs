namespace Foundatio.Queues;

public class SQSQueueConnectionStringBuilder : AmazonConnectionStringBuilder
{
    public SQSQueueConnectionStringBuilder()
    {
    }

    public SQSQueueConnectionStringBuilder(string connectionString) : base(connectionString)
    {
    }
}
