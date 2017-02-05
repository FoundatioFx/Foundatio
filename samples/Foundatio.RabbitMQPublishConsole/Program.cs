using System;
using Foundatio.Messaging;

namespace Foundatio.RabbitMQPublishConsole {
    public class Program {
        public static void Main(string[] args) {
            Console.WriteLine("Publisher...");
            IMessageBus messageBus = new RabbitMQMessageBus("amqp://localhost", "FoundatioQueue", "FoundatioQueueRoutingKey", "FoundatioExchange");

            Console.WriteLine("Enter the messages to send (press CTRL+Z) to exit :");

            string message;
            do {
                message = Console.ReadLine();
                messageBus.PublishAsync(message);
            } while (message != null);

            messageBus.Dispose();
        }
    }
}