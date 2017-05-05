using System;
using Foundatio.Messaging;

namespace Foundatio.RabbitMQSubscribeConsole {
    public class Program {
        public static void Main(string[] args) {
            Console.WriteLine("Waiting to receive messages....");

            IMessageBus messageBus = new RabbitMQMessageBus(new RabbitMQMessageBusOptions { ConnectionString = "amqp://localhost", Topic = "FoundatioQueue", ExchangeName = "FoundatioExchange" });
            messageBus.SubscribeAsync<string>(msg => { Console.WriteLine(msg); }).GetAwaiter().GetResult();
            Console.ReadLine();
        }
    }
}