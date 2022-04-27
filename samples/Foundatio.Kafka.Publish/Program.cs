using System;
using System.Threading.Tasks;
using Foundatio.Messaging;

namespace Foundatio.Kafka.Publish {
    public class Program {
        public static async Task Main(string[] args) {
            Console.WriteLine("Enter the message and press enter to send:");

            using (var messageBus = new KafkaMessageBus(new KafkaMessageBusOptions { ConnectionString = "amqp://localhost:5672" })) {
                string message;
                do {
                    message = Console.ReadLine();
                    var delay = TimeSpan.FromSeconds(1);
                    var body = new MyMessage { Hey = message };
                    await messageBus.PublishAsync(body, delay);
                    Console.WriteLine("Message sent. Enter new message or press enter to exit:");
                } while (!String.IsNullOrEmpty(message));
            }
        }
    }
}