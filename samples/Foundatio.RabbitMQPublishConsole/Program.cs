using System;
using Foundatio.Messaging;

namespace Foundatio.RabbitMQPublishConsole {
    public class Program {
        public static void Main(string[] args) {
            IMessageBus messageBus;
            string message;
            Console.WriteLine("Publisher...");
            messageBus = new RabbitMQMessageBus("guest", "guest", "FoundatioQueue", "FoundatioQueueRoutingKey",
                    "FoundatioExchange", true, true, false, false);

            Console.WriteLine("Enter the messages to send (press CTRL+Z) to exit :");

            do {
                message = Console.ReadLine();
                    messageBus.PublishAsync(message);
            } while (message != null);

            messageBus.Dispose();
        }
    }
}