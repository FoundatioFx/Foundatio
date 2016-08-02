using System;
using Foundatio.Messaging;

namespace Foundatio.RabbitMQSubscribeConsole {
    public class Program {
        public static void Main(string[] args) {
            Console.WriteLine("Subscriber...." +  args[0]);
            IMessageBus messageBus = new RabbitMQMessageBus("guest", "guest", "FoundatioExchangeFanout", args[0]);

            messageBus.Subscribe<string>(msg => { Console.WriteLine(msg); });
            Console.ReadLine();
        }
    }
}