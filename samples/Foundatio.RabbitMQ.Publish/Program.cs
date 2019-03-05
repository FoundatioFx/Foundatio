using System;
using System.Threading.Tasks;
using Foundatio.Messaging;

namespace Foundatio.RabbitMQ.Publish {
    public class Program {
        public static async Task Main(string[] args) {
            Console.WriteLine("Enter the message and press enter to send:");

            using (var messageBus = new RabbitMQMessageBus(new RabbitMQMessageBusOptions { ConnectionString = "amqp://localhost:5672" })) {
                string message;
                do {
                    message = Console.ReadLine();
                    var delay = TimeSpan.FromSeconds(10);
                    await messageBus.PublishAsync(message, delay);
                    Console.WriteLine("Message sent. Enter new message or press enter to exit:");
                } while (!String.IsNullOrEmpty(message));
            }
        }
    }
}