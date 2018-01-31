namespace Foundatio.Queues {
    internal class SQSQueueConnectionStringBuilder : AmazonConnectionStringBuilder {
        public SQSQueueConnectionStringBuilder()
        {
        }

        public SQSQueueConnectionStringBuilder(string connectionString) : base(connectionString)
        {
        }
    }
}
