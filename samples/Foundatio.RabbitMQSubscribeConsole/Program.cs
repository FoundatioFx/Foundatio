using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Foundatio.RabbitMQ.Messaging;
namespace Foundatio.RabbitMQSubscribeConsole
{
    public class Program
    {
        public static void Main(string[] args)
        {
             IMessageBus messageBus = new RabbitMQMessageService("guest", "guest", "FoundatioQueue", "FoundatioQueueRoutingKey", "FoundatioExchange", true, true,
                 false, false, null, TimeSpan.FromMilliseconds(50));
             Console.WriteLine("Subscriber....");
                messageBus.Subscribe<string>(msg => {
                    Console.WriteLine(msg);
                });
             Console.ReadLine();
        }
    }
}
