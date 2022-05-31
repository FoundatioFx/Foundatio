using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Confluent.Kafka;
using Foundatio.Messaging;
using Microsoft.Extensions.Logging;

namespace Foundatio.Kafka.Subscribe;

public class Program {
    public static async Task Main() {
        using var loggerFactory = LoggerFactory.Create(builder => {
            builder.AddConsole().SetMinimumLevel(LogLevel.Debug);
        });
        var logger = loggerFactory.CreateLogger<Program>();

        logger.LogInformation("Waiting to receive messages, press enter to quit...");

        var tasks = new List<Task>();
        var messageBuses = new List<IMessageBus>();
        for (int i = 0; i < 1; i++) {
            var messageBus = new KafkaMessageBus(new KafkaMessageBusOptions {
                BootstrapServers = "localhost:9092",
                Topic = "sample-topic",
                GroupId = Guid.NewGuid().ToString(),
                AutoOffSetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = true,
                EnableAutoOffsetStore = true,
                AllowAutoCreateTopics = true,
                LoggerFactory = loggerFactory
            });

            messageBuses.Add(messageBus);
            tasks.Add(messageBus.SubscribeAsync<MyMessage>(msg => {
                logger.LogInformation($"Got subscriber {messageBus.MessageBusId} message: {msg.Hey}");
            }));
        }

        await Task.WhenAll(tasks);
        Console.ReadLine();
        foreach (var messageBus in messageBuses)
            messageBus.Dispose();
    }
}