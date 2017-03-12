using System;
using Foundatio.Messaging;

namespace Foundatio.RabbitMQSubscribeConsole {
    public class Program {
        public static void Main(string[] args) {
            IMessageBus messageBus = new RabbitMQMessageBus("amqp://localhost", "FoundatioQueue", "FoundatioQueueRoutingKey", "FoundatioExchange", defaultMessageTimeToLive: TimeSpan.FromMilliseconds(50));
            Console.WriteLine("Subscriber....");
            messageBus.SubscribeAsync<string>(msg => { Console.WriteLine(msg); }).GetAwaiter().GetResult();
            Console.ReadLine();
        }
    }
}