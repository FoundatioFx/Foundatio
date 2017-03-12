using System;
using Foundatio.Messaging;

namespace Foundatio.RabbitMQSubscribeConsole {
    public class Program {
        public static void Main(string[] args) {
            Console.WriteLine("Subscriber....");
            messageBus.SubscribeAsync<string>(msg => { Console.WriteLine(msg); }).GetAwaiter().GetResult();
            Console.ReadLine();
        }
    }
}