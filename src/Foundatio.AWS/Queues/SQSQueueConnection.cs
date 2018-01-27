namespace Foundatio.Queues {
    internal class SQSQueueConnection : AmazonConnection {
        public static SQSQueueConnection Parse(string connectionString) {
            return Parse<SQSQueueConnection>(connectionString);
        }
    }
}
