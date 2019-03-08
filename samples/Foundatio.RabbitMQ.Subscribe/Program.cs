using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Foundatio.Messaging;

namespace Foundatio.RabbitMQ.Subscribe {
    public class Program {
        public static async Task Main(string[] args) {
            Console.WriteLine("Waiting to receive messages, press enter to quit...");

            var tasks = new List<Task>();
            var messageBuses = new List<IMessageBus>();
            for (int i = 0; i < 3; i++) {
                var messageBus = new RabbitMQMessageBus(new RabbitMQMessageBusOptions { ConnectionString = "amqp://localhost:5672" });
                messageBuses.Add(messageBus);
                tasks.Add(messageBus.SubscribeAsync<MyMessage>(msg => { Console.WriteLine($"Got subscriber {messageBus.MessageBusId} message: {msg.Hey}"); }));
            }
            await Task.WhenAll(tasks);
            Console.ReadLine();
            foreach (var messageBus in messageBuses)
                messageBus.Dispose();
       }
    }
}