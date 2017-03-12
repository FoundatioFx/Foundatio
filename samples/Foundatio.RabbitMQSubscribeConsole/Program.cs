using System;
using Foundatio.Messaging;

namespace Foundatio.RabbitMQSubscribeConsole {
    public class Program {
        public static void Main(string[] args) {
            Console.WriteLine("Subscriber....");
            IMessageBus messageBus = new RabbitMQMessageBus("amqp://localhost", "FoundatioQueue", "FoundatioQueueRoutingKey", "FoundatioExchange");
            messageBus.SubscribeAsync<string>(msg => { Console.WriteLine(msg); }).GetAwaiter().GetResult();
            Console.ReadLine();
        }
    }
}