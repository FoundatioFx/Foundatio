using System;
using Foundatio.Messaging;

namespace Foundatio.RabbitMQSubscribeConsole {
    public class Program {
        public static void Main(string[] args) {
            Console.WriteLine("Subscriber....");
            IMessageBus messageBus = new RabbitMQMessageBus("guest", "guest", "FoundatioQueue", "FoundatioQueueRoutingKey",
                "FoundatioExchange", true, true, false, false);

            messageBus.Subscribe<string>(msg => { Console.WriteLine(msg); });
            Console.ReadLine();
        }
    }
}