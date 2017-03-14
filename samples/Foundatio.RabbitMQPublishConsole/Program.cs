using System;
using Foundatio.Messaging;

namespace Foundatio.RabbitMQPublishConsole {
    public class Program {
        public static void Main(string[] args) {
            Console.WriteLine("Enter the message and press enter to send:");

            IMessageBus messageBus = new RabbitMQMessageBus("amqp://localhost", "FoundatioQueue", "FoundatioExchange");
            string message;
            do {
                message = Console.ReadLine();
                messageBus.PublishAsync(message);
            } while (message != null);

            messageBus.Dispose();
        }
    }
}