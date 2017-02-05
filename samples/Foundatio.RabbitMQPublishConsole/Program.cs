using System;
using Foundatio.Messaging;

namespace Foundatio.RabbitMQPublishConsole {
    public class Program {
        public static void Main(string[] args) {
            IMessageBus messageBus = new RabbitMQMessageBus("amqp://localhost", "FoundatioQueue", "FoundatioQueueRoutingKey", "FoundatioExchange", defaultMessageTimeToLive: TimeSpan.FromMilliseconds(50));
            string input;
            Console.WriteLine("Publisher...");
            Console.WriteLine("Enter the messages to send (press CTRL+Z) to exit :");
            do {
                input = Console.ReadLine();
                messageBus.PublishAsync(input);
            } while (input != null);
            messageBus.Dispose();
        }
    }
}