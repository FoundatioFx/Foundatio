using System;
using Foundatio.Messaging;

namespace Foundatio.RabbitMQPublishConsole {
    public class Program {
        public static void Main(string[] args) {
            IMessageBus messageBus = new RabbitMQMessageBus("guest", "guest", "FoundatioQueue", "FoundatioQueueRoutingKey", "FoundatioDelayedExchange", true, true, false, false, null, TimeSpan.FromMilliseconds(50));
            string input;
            Console.WriteLine("Publisher...");
            Console.WriteLine("Enter the messages to send (press CTRL+Z) to exit :");
            do {
                input = Console.ReadLine();
                messageBus.PublishAsync(input, TimeSpan.FromMilliseconds(60000));
            } while (input != null);
            messageBus.Dispose();
        }
    }
}