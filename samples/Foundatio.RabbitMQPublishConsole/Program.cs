using System;
using Foundatio.Messaging;

namespace Foundatio.RabbitMQPublishConsole {
    public class Program {
        public static void Main(string[] args) {
            IMessageBus messageBus;
            string message;
            Console.WriteLine("Publisher...");

            Console.WriteLine("Enter 1 for delayed exchange type , 2 for regular exchange type. Hit Enter.");
            var exchangeType = Console.ReadLine();
            if (string.Equals(exchangeType, "1")) {
                messageBus = new RabbitMQMessageBus("guest", "guest", "FoundatioQueue", "FoundatioQueueRoutingKey",
                    "FoundatioDelayedExchange", true, true, true, false, false, null, TimeSpan.FromMilliseconds(50));
                
            } else {
                messageBus = new RabbitMQMessageBus("guest", "guest", "FoundatioQueue", "FoundatioQueueRoutingKey",
                    "FoundatioExchange", false, true, true, false, false, null, TimeSpan.FromMilliseconds(50));
            }
            Console.WriteLine("Enter the messages to send (press CTRL+Z) to exit :");
            do {
                message = Console.ReadLine();
                if (string.Equals(exchangeType, "1")) {
                    messageBus.PublishAsync(message/*, TimeSpan.FromMilliseconds(60000)*/);
                } else {
                    messageBus.PublishAsync(message);
                }
            } while (message != null);
            messageBus.Dispose();
        }
    }
}