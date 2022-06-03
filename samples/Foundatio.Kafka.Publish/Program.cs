using System;
using System.Net;
using System.Threading.Tasks;
using Foundatio.Messaging;
using Microsoft.Extensions.Logging;

namespace Foundatio.Kafka.Publish;

public class Program {
    public static async Task Main() {
        using var loggerFactory = LoggerFactory.Create(builder => {
            builder.AddConsole().SetMinimumLevel(LogLevel.Debug);
        });
        var logger = loggerFactory.CreateLogger<Program>();

        logger.LogInformation("Enter the message and press enter to send:");
        using var messageBus = new KafkaMessageBus(new KafkaMessageBusOptions {
            BootstrapServers = "localhost:9092",
            Topic = "sample-topic",
            LoggerFactory = loggerFactory,
        });

        string message;
        do {
            message = Console.ReadLine();
            await messageBus.PublishAsync(new MyMessage { Hey = message });

            logger.LogInformation("Message sent. Enter new message or press enter to exit:");
        } while (!String.IsNullOrEmpty(message));
    }
}