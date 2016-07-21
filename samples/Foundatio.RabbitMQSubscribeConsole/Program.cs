using System;
using Foundatio.Messaging;

namespace Foundatio.RabbitMQSubscribeConsole {
    public class Program {
        public static void Main(string[] args) {
            IMessageBus messageBus;
            Console.WriteLine("Subscriber....");

            Console.WriteLine("Enter 1 for delayed exchange type , 2 for regular exchange type");
            var exchangeType = Console.ReadLine();
            if (string.Equals(exchangeType, "1")) {
                messageBus = new RabbitMQMessageBus("guest", "guest", "FoundatioQueue", "FoundatioQueueRoutingKey",
                    "FoundatioDelayedExchange", true, true, true, false, false, null, TimeSpan.FromMilliseconds(50));

            } else {
                messageBus = new RabbitMQMessageBus("guest", "guest", "FoundatioQueue", "FoundatioQueueRoutingKey",
                    "FoundatioExchange", false, true, true, false, false, null, TimeSpan.FromMilliseconds(50));
            }

            messageBus.Subscribe<string>(msg => { Console.WriteLine(msg); });
            Console.ReadLine();
        }
    }
}